using NLog;

namespace BDSM.Lib;
internal class ConsolidatedLogger
{
	public string Prefix { get; init; }
	internal ILogger? ParentLogger { get; private set; }
	public void SetLogger(ILogger logger) => ParentLogger = ParentLogger is null ? logger : throw new InvalidOperationException();
	public ConsolidatedLogger(ILogger? logger, string prefix) { ParentLogger = ParentLogger is null ? logger : throw new InvalidOperationException(); Prefix = prefix; }
	public void Log(LogLevel loglevel, string message, string? prefix = null) => ParentLogger?.Log(loglevel, message, prefix);
	public void Log(LogLevel loglevel, Exception ex, string? prefix = null) => ParentLogger?.Log(loglevel, ex, prefix);
	public void Trace(string message, string? prefix = null) { message = $"{prefix + " " + message ?? message}"; Log(LogLevel.Trace, message); }
	public void Debug(string message, string? prefix = null) { message = prefix + " " + message ?? message; Log(LogLevel.Debug, message); }
	public void Info(string message, string? prefix = null) { message = prefix + " " + message ?? message; Log(LogLevel.Info, message); }
	public void Warn(string message, string? prefix = null) { message = prefix + " " + message ?? message; Log(LogLevel.Warn, message); }
	public void Error(string message, string? prefix = null) { message = prefix + " " + message ?? message; Log(LogLevel.Error, message); }
	public void Fatal(string message, string? prefix = null) { message = prefix + " " + message ?? message; Log(LogLevel.Fatal, message); }
}