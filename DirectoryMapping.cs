namespace BDSM;
public readonly record struct PathMapping
{
	public required string GamePath { get; init; }
	public required string RootPath { get; init; }
	public required string LocalRelativePath { get; init; }
	public required string RemoteRelativePath { get; init; }
	public string LocalFullPath { get => GamePath + LocalRelativePath; }
	public string RemoteFullPath { get => RootPath + RemoteRelativePath; }
	public string LocalFullPathLower { get => (GamePath + LocalRelativePath).ToLower(); }
	public string RemoteFullPathLower { get => (RootPath + RemoteRelativePath).ToLower(); }
	public string FileName { get => LocalFullPath.Split('\\').Last(); }
	public required readonly bool DeleteClientFiles { get; init; }
	public required readonly long? FileSize { get; init; }
}