namespace BDSM;
public record Exceptions
{
	public class BDSMInternalFaultException : Exception
	{
		const string BugReportSuffix = " Please file a bug report and provide this information. https://github.com/RobotsOnDrugs/BDSM/issues";
		public BDSMInternalFaultException() { }
		public BDSMInternalFaultException(string? message) : base(message + BugReportSuffix) { }
		public BDSMInternalFaultException(string? message, bool include_bug_report_link) : base(message + (include_bug_report_link ? "" : BugReportSuffix)) { }
		public BDSMInternalFaultException(string? message, Exception? innerException) : base(message, innerException) { }
	}
	public class FTPOperationException : Exception
	{
		public FTPOperationException() { }
		public FTPOperationException(string? message) : base(message) { }
		public FTPOperationException(string? message, Exception? innerException) : base(message, innerException) { }
	}
	public class FTPConnectionException : FTPOperationException
	{
		public FTPConnectionException() { }

		public FTPConnectionException(string? message) : base(message) { }

		public FTPConnectionException(string? message, Exception? innerException) : base(message, innerException) { }
	}
	public class FTPTaskAbortedException : FTPOperationException
	{
		public FTPTaskAbortedException() : base() { }

		public FTPTaskAbortedException(string? message) : base(message) { }

		public FTPTaskAbortedException(string? message, Exception? innerException) : base(message, innerException) { }
		public override Dictionary<string, string> Data { get; } = new() { { "File", "" } };
	}
}
