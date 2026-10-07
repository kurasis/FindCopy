using FindCopy.Bench;

return args.Length >= 2 && args[1] == "--extended-acceptance"
    ? ExtendedAcceptance.Execute(args) : BenchmarkRunner.Execute(args);
