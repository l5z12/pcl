using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System.Security.Cryptography;

namespace PclPatcher.Core;

public sealed record Inspection(string Assembly, string Version, int Size, string Sha256, string Edition, int Patches);
public sealed record PatchOutput(byte[] Bytes, string Sha256, IReadOnlyList<string> Log);
public sealed class CompatibilityException(string message) : Exception(message);

public static class PatchService
{
    public const int MaxFileSize = 128 * 1024 * 1024;
    public const string SupportedVersion = "2.99.0.0";

    public static Inspection Inspect(byte[] bytes)
    {
        if (OpenBundle(bytes) is { } bundle) return CommunityEdition.Inspect(bytes, bundle);
        using var module = Load(bytes);
        Validate(module);
        return new(module.Assembly!.Name.String, module.Assembly.Version.ToString(),
            bytes.Length, Hash(bytes), "PCL2", 5);
    }

    public static PatchOutput Patch(byte[] bytes)
    {
        if (OpenBundle(bytes) is { } bundle) return CommunityEdition.Patch(bundle);
        using var module = Load(bytes);
        Validate(module);
        var log = new List<string>();
        try
        {
            new Patcher(module, log.Add).Apply();
            using var output = new MemoryStream();
            module.Write(output);
            var result = output.ToArray();
            Verify(result);
            log.Add("Output reopened and all five patch operations verified.");
            return new(result, Hash(result), log);
        }
        catch (CompatibilityException) { throw; }
        catch (Exception e)
        {
            throw new CompatibilityException($"This executable doesn't match the expected patch layout. No output was created. ({e.Message})");
        }
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void CheckExecutable(byte[] bytes)
    {
        if (bytes.Length > MaxFileSize)
            throw new CompatibilityException("This file exceeds the 128 MB limit.");
        if (bytes.Length < 64 || bytes[0] != 'M' || bytes[1] != 'Z')
            throw new CompatibilityException("This isn't a Windows executable. Choose your original PCL2 .exe file.");
    }

    // PCL CE ships as a .NET single-file bundle; PCL2 is a plain .NET Framework executable.
    private static SingleFileBundle? OpenBundle(byte[] bytes)
    {
        CheckExecutable(bytes);
        try { return SingleFileBundle.TryOpen(bytes); }
        catch (InvalidDataException e)
        {
            throw new CompatibilityException($"This single-file executable can't be read: {e.Message}. Choose an original PCL CE {CommunityEdition.SupportedVersion} executable.");
        }
    }

    private static ModuleDefMD Load(byte[] bytes)
    {
        CheckExecutable(bytes);
        try { return ModuleDefMD.Load(bytes); }
        catch (Exception)
        {
            throw new CompatibilityException("This file isn't a readable .NET executable. Choose an original PCL2 2.99.0.0 or PCL CE 2.15.1 executable.");
        }
    }

    private static TypeDef RequireType(ModuleDef module, string name) =>
        module.Find("PCL." + name, false)
        ?? throw new CompatibilityException("This executable isn't a supported PCL2 build (missing " + name + ").");

    private static void Validate(ModuleDefMD module)
    {
        var secret = RequireType(module, "ModSecret");
        var restrict = secret.FindField("RestrictMcDownload");
        if (restrict?.Constant?.Value is not bool value)
            throw new CompatibilityException("The RestrictMcDownload flag was not found in this executable.");
        var pack = RequireType(module, "ModModpack");
        if (!value || pack.FindMethod("ModpackInstallDispatch") != null)
            throw new CompatibilityException("This file is already patched or has no download restriction. Choose an unmodified original.");
        if (module.Assembly?.Version.ToString() != SupportedVersion)
            throw new CompatibilityException($"PCL2 {module.Assembly?.Version} is not supported. This patch targets version {SupportedVersion}.");

        var loader = RequireType(module, "PageDownloadInstall").FindMethod("LoaderInit");
        if (loader?.HasBody != true || !loader.Body.Instructions.Select((x, i) => (x, i)).Any(p =>
            p.x.OpCode.Code == Code.Ldstr && p.i + 12 < loader.Body.Instructions.Count &&
            loader.Body.Instructions[p.i + 12].OpCode.Code == Code.Newobj &&
            loader.Body.Instructions[p.i + 12].Operand is IMethod ctor &&
            ctor.DeclaringType.Name.String.Contains("LoaderTask")))
            throw new CompatibilityException("The download loader doesn't match the expected original.");
        if (RequireType(module, "ModDownload").FindField("DlClientListLoader") is not { IsStatic: true })
            throw new CompatibilityException("The Minecraft version loader is missing.");
        var modify = RequireType(module, "PageDownloadModify");
        if (!modify.Methods.Where(m => m.HasBody).Any(m =>
            m.Body.Instructions.Select((x, i) => (x, i)).Any(p =>
                p.x.Operand is IMethod get && get.Name == "get_CardMinecraft" &&
                p.i + 2 < m.Body.Instructions.Count &&
                m.Body.Instructions[p.i + 1].OpCode.Code == Code.Ldc_I4_2 &&
                m.Body.Instructions[p.i + 2].Operand is IMethod set && set.Name == "set_Visibility")))
            throw new CompatibilityException("The Minecraft card doesn't match the expected original.");
        var install = pack.Methods.SingleOrDefault(m => m.Name == "ModpackInstall" &&
            m.IsStatic && m.MethodSig.Params.Count == 3 && m.MethodSig.Params.All(p => p.ElementType == ElementType.String));
        if (install?.HasBody != true || install.Body.Variables.Count < 5 ||
            install.Body.Variables[0].Type.FullName != install.ReturnType.FullName ||
            install.Body.Variables[1].Type.FullName != "System.IO.Compression.ZipArchive" ||
            install.Body.Variables[2].Type.ElementType != ElementType.String ||
            install.Body.Variables[4].Type.ElementType != ElementType.I4 ||
            install.ReturnType.FullName != "PCL.ModLoader/LoaderCombo`1<System.String>")
            throw new CompatibilityException("The modpack installer has an unsupported method layout.");
        if (!install.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr && i.Operand is string s && s.Contains("qwq")))
            throw new CompatibilityException("The expected modpack restriction was not found.");
        if (!pack.Methods.Any(m => m.Name == "ModpackInstall" && m.IsStatic &&
            m.HasBody && m.MethodSig.Params.Count == 0 && m.ReturnType.ElementType == ElementType.Void))
            throw new CompatibilityException("The modpack file picker entry point is missing.");
        var signatures = new Dictionary<string, int>
        {
            ["InstallPackCurseForge"] = 5, ["InstallPackHMCL"] = 3, ["InstallPackMMC"] = 3,
            ["InstallPackMCBBS"] = 4, ["InstallPackModrinth"] = 5,
            ["InstallPackLauncherPack"] = 3, ["InstallPackCompress"] = 2
        };
        foreach (var (name, count) in signatures)
        {
            var method = pack.FindMethod(name);
            if (method == null || !method.IsStatic || method.MethodSig.Params.Count != count ||
                method.ReturnType.FullName != install.ReturnType.FullName ||
                method.MethodSig.Params[0].ElementType != ElementType.String ||
                method.MethodSig.Params[1].FullName != install.Body.Variables[1].Type.FullName ||
                method.MethodSig.Params.Skip(2).Any(p => p.ElementType != ElementType.String))
                throw new CompatibilityException($"Unsupported modpack handler: {name}.");
        }
        if (RequireType(module, "ModJava").FindMethod("ManuallyImportJava")?.HasBody != true)
            throw new CompatibilityException("File picker references are missing.");
    }

    public static void Verify(byte[] bytes)
    {
        using var module = Load(bytes);
        void Check(bool success, string label)
        {
            if (!success) throw new CompatibilityException($"Output verification failed: {label}. No download is available.");
        }
        Check(RequireType(module, "ModSecret").FindField("RestrictMcDownload")?.Constant?.Value is false, "download flag");
        var loader = RequireType(module, "PageDownloadInstall").FindMethod("LoaderInit");
        Check(loader?.Body?.Instructions.Any(i => i.OpCode.Code == Code.Ldsfld &&
            i.Operand is IField f && f.FullName.Contains("PCL.ModDownload::DlClientListLoader")) == true, "download loader");
        var visibility = RequireType(module, "PageDownloadModify").Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select((x, i) => (m, x, i)))
            .Where(p => p.x.Operand is IMethod get && get.Name == "get_CardMinecraft" &&
                p.i + 2 < p.m.Body.Instructions.Count &&
                p.m.Body.Instructions[p.i + 2].Operand is IMethod set && set.Name == "set_Visibility").ToList();
        Check(visibility.Any(p => p.m.Body.Instructions[p.i + 1].OpCode.Code == Code.Ldc_I4_0) &&
            visibility.All(p => p.m.Body.Instructions[p.i + 1].OpCode.Code != Code.Ldc_I4_2), "Minecraft card");
        var pack = RequireType(module, "ModModpack");
        var dispatch = pack.FindMethod("ModpackInstallDispatch");
        Check(dispatch?.Body?.Instructions.Count(i => i.OpCode.Code == Code.Call) == 7, "seven modpack handlers");
        var install = pack.Methods.Single(m => m.Name == "ModpackInstall" && m.MethodSig.Params.Count == 3);
        Check(install.Body.Instructions.Any(i => i.OpCode.Code == Code.Call &&
            i.Operand is IMethod m && m.Name == "ModpackInstallDispatch"), "modpack dispatch");
        Check(install.Body.ExceptionHandlers.All(h => h.HandlerType != ExceptionHandlerType.Finally || h.HandlerEnd != null), "finally boundaries");
        var picker = pack.Methods.Single(m => m.Name == "ModpackInstall" && m.MethodSig.Params.Count == 0);
        Check(picker.Body.Instructions.Any(i => i.OpCode.Code == Code.Call && i.Operand is IMethod m && m.Name == "SelectFile") &&
            picker.Body.Instructions.Any(i => i.OpCode.Code == Code.Call && i.Operand is IMethod m && m.Name == "ModpackInstall"),
            "file picker");
    }
}
