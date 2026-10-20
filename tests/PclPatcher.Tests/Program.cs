using dnlib.DotNet;
using dnlib.DotNet.Emit;
using PclPatcher.Core;

var passed = 0;
void Test(string name, Action action)
{
    action();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
void Reject(byte[] bytes, string expected)
{
    try { PatchService.Patch(bytes); throw new Exception("Invalid input was accepted"); }
    catch (CompatibilityException e) { Assert(e.Message.Contains(expected, StringComparison.OrdinalIgnoreCase), e.Message); }
}
Test("Empty file rejected", () => Reject([], "Windows executable"));
Test("Non-PE file rejected", () => Reject(new byte[100], "Windows executable"));
Test("Corrupt PE rejected", () => { var b = new byte[100]; b[0] = 77; b[1] = 90; Reject(b, "readable .NET"); });
Test("Unrelated .NET assembly rejected", () => Reject(File.ReadAllBytes(typeof(PatchService).Assembly.Location), "supported PCL2"));

if (args.Length != 0)
{
    var original = File.ReadAllBytes(args[0]);
    if (args.Contains("--inspect"))
    {
        using var diagnostic = ModuleDefMD.Load(original);
        foreach (var m in diagnostic.Find("PCL.ModModpack", false).Methods.Where(m => m.Name == "ModpackInstall" || m.Name.String.StartsWith("InstallPack")))
        {
            Console.WriteLine($"{m.FullName}; static={m.IsStatic}");
            if (m.Name == "ModpackInstall" && m.HasBody)
                foreach (var v in m.Body.Variables) Console.WriteLine($"  local {v.Index}: {v.Type.FullName} ({v.Type.ElementType})");
        }
        return;
    }
    var before = PatchService.Hash(original);
    PatchOutput? patched = null;
    Test("Original PCL2 passes compatibility", () =>
    {
        var inspection = PatchService.Inspect(original);
        Assert(inspection.Version == "2.99.0.0", "Unexpected version");
        Console.WriteLine($"  {inspection.Assembly} {inspection.Version}, {inspection.Size} bytes");
    });
    Test("All five patches applied and output reopened", () =>
    {
        patched = PatchService.Patch(original);
        Assert(patched.Log.Count == 6, "Incomplete patch report");
        Assert(patched.Sha256 != before, "Output unchanged");
        foreach (var line in patched.Log) Console.WriteLine("  " + line);
    });
    Test("Original bytes and source file remain unchanged", () =>
        Assert(PatchService.Hash(original) == before && PatchService.Hash(File.ReadAllBytes(args[0])) == before, "Original modified"));
    Test("Already patched file rejected", () => Reject(patched!.Bytes, "already patched"));
    Test("Unsupported version rejected", () =>
    {
        using var mod = ModuleDefMD.Load(original);
        mod.Assembly.Version = new Version(2, 100, 0, 0);
        using var stream = new MemoryStream(); mod.Write(stream);
        Reject(stream.ToArray(), "not supported");
    });
    Test("Missing loader pattern rejected", () =>
    {
        using var mod = ModuleDefMD.Load(original);
        mod.Find("PCL.PageDownloadInstall", false).FindMethod("LoaderInit").Body = new CilBody();
        mod.Find("PCL.PageDownloadInstall", false).FindMethod("LoaderInit").Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        using var stream = new MemoryStream(); mod.Write(stream);
        Reject(stream.ToArray(), "download loader");
    });
    Test("Seven dispatch targets and argument signatures match", () =>
    {
        using var mod = ModuleDefMD.Load(patched!.Bytes);
        var dispatch = mod.Find("PCL.ModModpack", false).FindMethod("ModpackInstallDispatch");
        var instructions = dispatch.Body.Instructions;
        var targets = new[] { "InstallPackCurseForge", "InstallPackHMCL", "InstallPackMMC", "InstallPackMCBBS",
            "InstallPackModrinth", "InstallPackLauncherPack", "InstallPackCompress" };
        Assert(instructions.Where(i => i.OpCode.Code == Code.Call).Select(i => ((IMethod)i.Operand).Name.String)
            .SequenceEqual(targets), "Wrong dispatch order");
        for (var index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].OpCode.Code != Code.Call) continue;
            var method = (IMethod)instructions[index].Operand;
            for (var p = 0; p < method.MethodSig.Params.Count; p++)
            {
                var load = instructions[index - method.MethodSig.Params.Count + p];
                Assert(load.Operand is Parameter arg &&
                    arg.Type.FullName == method.MethodSig.Params[p].FullName, $"Argument mismatch: {method.Name}, {p}");
            }
        }
    });
    Test("Modpack return target lies outside finally regions", () =>
    {
        using var mod = ModuleDefMD.Load(patched!.Bytes);
        var m = mod.Find("PCL.ModModpack", false).Methods.Single(m => m.Name == "ModpackInstall" && m.MethodSig.Params.Count == 3);
        var ins = m.Body.Instructions;
        foreach (var eh in m.Body.ExceptionHandlers.Where(h => h.HandlerType == ExceptionHandlerType.Finally))
        {
            Assert(eh.HandlerEnd != null, "Unbounded finally");
            foreach (var leave in ins.Where(i => i.OpCode.Code is Code.Leave or Code.Leave_S))
            {
                var targetIndex = ins.IndexOf((Instruction)leave.Operand);
                Assert(targetIndex < ins.IndexOf(eh.HandlerStart) || targetIndex >= ins.IndexOf(eh.HandlerEnd!), "Leave lands inside finally");
            }
        }
    });
    Directory.CreateDirectory(".test-artifacts");
    File.WriteAllBytes(".test-artifacts/Plain Craft Launcher 2.patched.exe", patched!.Bytes);
    Console.WriteLine("Patched SHA-256: " + patched.Sha256);
}
else Console.WriteLine("Pass an original PCL2 2.99.0.0 executable path to run the integration checks.");
Console.WriteLine($"{passed} checks passed.");
