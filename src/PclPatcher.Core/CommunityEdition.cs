using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace PclPatcher.Core;

// PCL CE 2.15.1 (PCL-Community/PCL-CE#3673, #3676) sends every Minecraft download, install, repair, reset and
// modpack install through PCL.RestrictedFeaturePolicy, which blocks them when the Windows region settings point
// to mainland China or are inconclusive. Fixing the policy's two answers lifts every gate at once.
internal static class CommunityEdition
{
    public const string SupportedVersion = "2.15.1.0";
    public const string MainAssembly = "Plain Craft Launcher 2.dll";
    private const string Policy = "PCL.RestrictedFeaturePolicy";
    private const string Assess = "PCL.Core.App.Region.RegionAssessment PCL.RestrictedFeaturePolicy::Assess()";
    private const string AssessmentRestricted = "System.Boolean PCL.Core.App.Region.RegionAssessment::get_IsRestricted()";

    public static Inspection Inspect(byte[] bytes, SingleFileBundle bundle)
    {
        using var module = Load(bundle);
        Validate(module);
        return new(module.Assembly!.Name.String, module.Assembly.Version.ToString(),
            bytes.Length, PatchService.Hash(bytes), "PCL CE", 2);
    }

    public static PatchOutput Patch(SingleFileBundle bundle)
    {
        using var module = Load(bundle);
        var gates = Validate(module);
        var log = new List<string>();
        try
        {
            var policy = module.Find(Policy, false);
            ReturnConstant(policy.FindMethod("Check"), OpCodes.Ldc_I4_1);
            log.Add($"[1] RestrictedFeaturePolicy.Check → True ({gates} feature gates)");
            ReturnConstant(policy.FindMethod("get_IsRestricted"), OpCodes.Ldc_I4_0);
            log.Add("[2] RestrictedFeaturePolicy.IsRestricted → False (launch file completion)");
            using var output = new MemoryStream();
            // Keep the original tokens and heaps so only the two method bodies differ.
            module.Write(output, new ModuleWriterOptions(module) { MetadataOptions = { Flags = MetadataFlags.PreserveAll } });
            var result = bundle.Replace(MainEntry(bundle), output.ToArray());
            Verify(bundle, result, gates);
            log.Add($"Bundle reopened, both patch operations verified and the other {bundle.Entries.Count - 1} bundled files unchanged.");
            return new(result, PatchService.Hash(result), log);
        }
        catch (CompatibilityException) { throw; }
        catch (Exception e)
        {
            throw new CompatibilityException($"This executable doesn't match the expected patch layout. No output was created. ({e.Message})");
        }
    }

    private static SingleFileBundle.Entry MainEntry(SingleFileBundle bundle) =>
        bundle.Entries.Where(e => e.Path == MainAssembly).ToList() is [{ Type: SingleFileBundle.AssemblyType, CompressedSize: 0 } entry]
            ? entry
            : throw new CompatibilityException($"This single-file executable isn't a supported PCL CE build (missing {MainAssembly}).");

    private static ModuleDefMD Load(SingleFileBundle bundle)
    {
        var bytes = bundle.Read(MainEntry(bundle)).ToArray();
        try { return ModuleDefMD.Load(bytes); }
        catch (Exception)
        {
            throw new CompatibilityException($"The bundled {MainAssembly} isn't a readable .NET assembly.");
        }
    }

    private static int Validate(ModuleDefMD module)
    {
        if (module.Assembly?.Name != "Plain Craft Launcher 2")
            throw new CompatibilityException("This single-file executable isn't a supported PCL CE build.");
        var policy = module.Find(Policy, false)
            ?? throw new CompatibilityException("The region restriction policy was not found in this executable.");
        var check = policy.FindMethod("Check");
        var restricted = policy.FindMethod("get_IsRestricted");
        if (check?.HasBody != true || restricted?.HasBody != true)
            throw new CompatibilityException("The region restriction policy has an unsupported layout.");
        if (ReturnsConstant(check, Code.Ldc_I4_1) && ReturnsConstant(restricted, Code.Ldc_I4_0))
            throw new CompatibilityException("This file is already patched or has no download restriction. Choose an unmodified original.");
        if (module.Assembly.Version.ToString() != SupportedVersion)
            throw new CompatibilityException($"PCL CE {module.Assembly.Version} is not supported. This patch targets version {SupportedVersion}.");

        if (!check.IsStatic || check.ReturnType.ElementType != ElementType.Boolean ||
            !check.MethodSig.Params.Select(p => p.ElementType).SequenceEqual(new[] { ElementType.String, ElementType.Boolean }) ||
            !Calls(check, Assess) || !Calls(check, AssessmentRestricted))
            throw new CompatibilityException("The feature gate check doesn't match the expected original.");
        if (!restricted.IsStatic || restricted.ReturnType.ElementType != ElementType.Boolean ||
            !restricted.Body.Instructions.Select(i => i.OpCode.Code).SequenceEqual(new[] { Code.Call, Code.Callvirt, Code.Ret }) ||
            !Calls(restricted, Assess) || !Calls(restricted, AssessmentRestricted))
            throw new CompatibilityException("The restriction flag doesn't match the expected original.");
        if (policy.FindMethod("ThrowIfRestricted") is not { HasBody: true } throwIf || !throwIf.Body.Instructions.Any(i => i.Operand == check))
            throw new CompatibilityException("The restriction exception path doesn't match the expected original.");
        return CountGates(module, policy);
    }

    // Every route to the region decision must pass through Check or IsRestricted, or the patch would miss it.
    private static int CountGates(ModuleDef module, TypeDef policy)
    {
        var gates = 0;
        foreach (var method in module.GetTypes().Where(t => t != policy).SelectMany(t => t.Methods).Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not IMethod target) continue;
            if (target.DeclaringType?.FullName == Policy && target.Name != "Assess") gates++;
            else if (target.DeclaringType?.FullName == Policy || target.DeclaringType?.Namespace == "PCL.Core.App.Region")
                throw new CompatibilityException($"Found a region check outside the restriction policy ({method.FullName}).");
        }
        if (gates == 0) throw new CompatibilityException("No feature gates use the region restriction policy.");
        return gates;
    }

    private static void Verify(SingleFileBundle original, byte[] bytes, int gates)
    {
        void Check(bool success, string label)
        {
            if (!success) throw new CompatibilityException($"Output verification failed: {label}. No download is available.");
        }
        var bundle = SingleFileBundle.TryOpen(bytes)
            ?? throw new CompatibilityException("Output verification failed: bundle manifest. No download is available.");
        Check(bundle.Entries.Select(e => (e.Path, e.Type)).SequenceEqual(original.Entries.Select(e => (e.Path, e.Type))), "bundle manifest");
        Check(bundle.Entries.Zip(original.Entries).All(p => p.First.Offset % 4096 == p.Second.Offset % 4096 &&
            (p.First.Path == MainAssembly || bundle.Read(p.First).SequenceEqual(original.Read(p.Second)))), "bundled files");
        using var module = Load(bundle);
        var policy = module.Find(Policy, false);
        Check(module.Assembly.Version.ToString() == SupportedVersion, "assembly version");
        Check(ReturnsConstant(policy.FindMethod("Check"), Code.Ldc_I4_1), "feature gate check");
        Check(ReturnsConstant(policy.FindMethod("get_IsRestricted"), Code.Ldc_I4_0), "restriction flag");
        Check(CountGates(module, policy) == gates, "feature gate call sites");
    }

    private static bool Calls(MethodDef method, string target) =>
        method.Body.Instructions.Any(i => i.Operand is IMethod m && m.FullName == target);

    private static bool ReturnsConstant(MethodDef method, Code value) =>
        method.Body.Instructions.Select(i => i.OpCode.Code).SequenceEqual(new[] { value, Code.Ret });

    private static void ReturnConstant(MethodDef method, OpCode value)
    {
        method.Body = new CilBody();
        method.Body.Instructions.Add(Instruction.Create(value));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
}
