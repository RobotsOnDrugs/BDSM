using System.Text;

namespace BDSM;
public record Configuration
{
	public readonly record struct RepoConnectionInfo
	{
		public required string Address { get; init; }
		public required string Username { get; init; }
		public string EffectivePassword => Password ?? Encoding.UTF8.GetString(Convert.FromBase64String(PasswordB64 ?? ""));
		public required string? Password { get; init; }
		public required string? PasswordB64 { get; init; }
		public required int Port { get; init; }
		public required string RootPath { get; init; }
		public required int MaxConnections { get; init; }
	}
	public readonly record struct UserConfiguration
	{
		public required string GamePath { get; init; }
		public required RepoConnectionInfo ConnectionInfo { get; init; }
		public required bool PromptToContinue { get; init; }
		public required string[] BaseSideloaderDirectories { get; init; }
	}
}
