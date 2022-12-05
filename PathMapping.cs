namespace BDSM;
public readonly record struct PathMapping
{
	public required string GamePath { get; init; }
	public required string RootPath { get; init; }
	public required string LocalRelativePath { get; init; }
	public required string RemoteRelativePath { get; init; }
	public string LocalFullPath => Path.Combine(GamePath, LocalRelativePath);
	public string RemoteFullPath => Path.Combine(RootPath, RemoteRelativePath);
	public string LocalFullPathLower => Path.Combine(GamePath, LocalRelativePath).ToLower();
	public string RemoteFullPathLower => Path.Combine(RootPath, RemoteRelativePath).ToLower();
	public string FileName => LocalFullPath.Split('\\').Last();
	public required readonly bool DeleteClientFiles { get; init; }
	public required readonly long? FileSize { get; init; }
}