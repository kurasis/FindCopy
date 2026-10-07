using FindCopy.Bench;

static class BenchmarkSafetyTests
{
    public static void Run(Action<string, Action> test, string root)
    {
        test("S4 dense acceptance preserves added files and refuses mutation of their dataset", () =>
        {
            string work = OwnedWorkspace(root, "dense-extra"), dataset = Path.Combine(work, "dense-D");
            DatasetGenerator.Generate("D", dataset, 1, 16 * 1048576L, copies: 2);
            string document = Path.Combine(dataset, "user-document.txt"), second = Path.Combine(dataset, "d1.bin");
            File.WriteAllText(document, "unrelated user data");
            byte[] before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(second));
            RunRejected(work, "Refusing an unexpected dense dataset entry");
            Require(File.ReadAllText(document) == "unrelated user data" && Directory.Exists(dataset) &&
                before.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(second))),
                "acceptance deleted unrelated data or modified the rejected dataset");
        });
        test("S5 dense acceptance still completes and removes its generated pair", () =>
        {
            string work = OwnedWorkspace(root, "dense-clean");
            Require(ExtendedAcceptance.Execute(new[] { work, "--extended-acceptance", "dense", "16" }) == 0,
                "normal dense acceptance failed");
            Require(!Directory.Exists(Path.Combine(work, "dense-D")) && !File.Exists(Path.Combine(work, "dense-D.generated")),
                "owned dense data was not cleaned up");
        });
        test("S6 dense acceptance rejects directory links and preserves their targets", () =>
        {
            string work = OwnedWorkspace(root, "dense-link"), target = Path.Combine(root, "dense-link-target");
            Directory.CreateDirectory(target); string document = Path.Combine(target, "document.txt");
            File.WriteAllText(document, "outside dataset");
            string link = Path.Combine(work, "dense-D");
            if (OperatingSystem.IsWindows()) Native.Junction(target, link);
            else Directory.CreateSymbolicLink(link, target);
            try
            {
                RunRejected(work, "Refusing a linked dense dataset directory");
                Require(File.ReadAllText(document) == "outside dataset" && Directory.EnumerateFileSystemEntries(target).Count() == 1,
                    "linked target was modified");
            }
            finally { Directory.Delete(link); }
        });
        test("S7 dense acceptance preserves files added immediately before cleanup", () =>
        {
            string work = OwnedWorkspace(root, "dense-late"), document = Path.Combine(work, "dense-D", "late-document.txt");
            var output = Console.Out;
            var intercept = new CleanupIntercept(output, () => File.WriteAllText(document, "late user data"));
            Console.SetOut(intercept);
            try { RunRejected(work, "Refusing an unexpected dense dataset entry"); }
            finally { Console.SetOut(output); }
            Require(intercept.Injected && File.ReadAllText(document) == "late user data",
                "late unrelated file was deleted or cleanup was reported as successful");
        });
        test("S8 dense acceptance rejects marker links without creating or overwriting their targets", () =>
        {
            string work = OwnedWorkspace(root, "dense-marker-link"), target = Path.Combine(root, "unrelated-new-file.txt");
            string dataset = Path.Combine(work, "dense-D"), marker = dataset + ".generated";
            foreach (bool exists in new[] { false, true })
            {
                if (exists) File.WriteAllText(target, "unrelated existing data");
                File.CreateSymbolicLink(marker, target);
                try
                {
                    RunRejected(work, "Refusing a linked dataset marker");
                    Require(!Directory.Exists(dataset) && (exists ? File.ReadAllText(target) == "unrelated existing data" : !File.Exists(target)),
                        "generation followed the marker link");
                }
                finally { File.Delete(marker); }
            }
        });
    }

    private static string OwnedWorkspace(string root, string name)
    {
        string work = Path.Combine(root, name); Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "extended-acceptance-owned.txt"), "FindCopy extended acceptance v1");
        return work;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void RunRejected(string work, string reason)
    {
        var previous = Console.Error;
        using var error = new StringWriter(); Console.SetError(error);
        try
        {
            Require(ExtendedAcceptance.Execute(new[] { work, "--extended-acceptance", "dense", "16" }) == 1,
                "unsafe acceptance unexpectedly succeeded");
            Require(error.ToString().Contains(reason, StringComparison.Ordinal), "acceptance failed for the wrong reason: " + error);
        }
        finally { Console.SetError(previous); }
    }

    private sealed class CleanupIntercept(TextWriter output, Action inject) : TextWriter
    {
        public override System.Text.Encoding Encoding => output.Encoding;
        public bool Injected { get; private set; }
        public override void WriteLine(string? value)
        {
            output.WriteLine(value);
            if (value?.StartsWith("dense middle-difference:", StringComparison.Ordinal) == true)
            {
                inject(); Injected = true;
            }
        }
    }
}
