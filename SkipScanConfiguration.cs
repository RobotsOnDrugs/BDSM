namespace BDSM;
public readonly record struct SkipScanConfiguration
{
	public required bool SkipScan { get; init; }
	public required string[] FileMappings { get; init; }
}
