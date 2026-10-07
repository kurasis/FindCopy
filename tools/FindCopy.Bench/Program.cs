using FindCopy.Bench;
using System.Runtime.InteropServices;

Console.WriteLine($"RUNTIME: OS={RuntimeInformation.OSDescription}; OS architecture={RuntimeInformation.OSArchitecture}; process={RuntimeInformation.ProcessArchitecture}");
string? expectedArchitecture = Environment.GetEnvironmentVariable("FINDCOPY_EXPECTED_ARCH");
if (!string.IsNullOrEmpty(expectedArchitecture) && expectedArchitecture != RuntimeInformation.ProcessArchitecture.ToString())
    throw new InvalidOperationException("Unexpected benchmark process architecture: " + RuntimeInformation.ProcessArchitecture);

return args.Length >= 2 && args[1] == "--extended-acceptance"
    ? ExtendedAcceptance.Execute(args) : BenchmarkRunner.Execute(args);
