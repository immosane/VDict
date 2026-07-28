using VDict.RemediationFixture;

try
{
    FixtureCommand command = FixtureCommand.Parse(args);
    FixtureValidationReport report = command.Action switch
    {
        FixtureAction.GenerateAndVerify => RemediationFixtureGenerator.GenerateAndVerify(command.OutputDirectory),
        FixtureAction.Verify => RemediationFixtureValidator.Validate(command.OutputDirectory),
        _ => throw new ArgumentOutOfRangeException()
    };

    Console.WriteLine(
        $"VDict remediation fixture {report.GeneratorVersion}: " +
        $"{report.EntryCount} entries, {report.ResourceCount} resources, " +
        $"semantic SHA-256 {report.SemanticSha256}");
    Console.WriteLine($"Output: {Path.GetFullPath(command.OutputDirectory)}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Fixture generation failed: {exception.Message}");
    return 1;
}

internal enum FixtureAction
{
    GenerateAndVerify,
    Verify
}

internal sealed record FixtureCommand(FixtureAction Action, string OutputDirectory)
{
    public static FixtureCommand Parse(string[] args)
    {
        FixtureAction action = FixtureAction.GenerateAndVerify;
        string output = Path.Combine("artifacts", "remediation-fixture");
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "generate":
                case "all":
                    action = FixtureAction.GenerateAndVerify;
                    break;
                case "verify":
                    action = FixtureAction.Verify;
                    break;
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(
                        "Usage: dotnet run --project tools/VDict.RemediationFixture -- " +
                        "[generate|verify] [--output <directory>]");
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown fixture argument: {args[index]}");
            }
        }

        return new FixtureCommand(action, output);
    }
}
