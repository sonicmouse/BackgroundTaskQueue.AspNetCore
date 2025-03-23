using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace BackgroundTaskQueue.AspNetCore.Tests
{
	[ExcludeFromCodeCoverage]
	internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

	internal interface ITestLogger
	{
		List<LogEntry> Entries { get; }
	}

	[ExcludeFromCodeCoverage]
	internal sealed class TestLogger<T> : ILogger<T>, IDisposable, ITestLogger
	{
		public List<LogEntry> Entries { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this;

		public void Dispose() { }

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
			Exception? exception, Func<TState, Exception?, string> formatter) =>
			Entries.Add(new(logLevel, formatter(state, exception), exception));
	}

	[ExcludeFromCodeCoverage]
	internal sealed class CustomExceptionLogger : IOffloadWorkExceptionLogger
	{
		public static List<(Exception Exception, string? CategoryName)> Entries { get; } = new();

		public void Log(Exception exception, string? categoryName)
		{
			Entries.Add((exception, categoryName));
		}

		public static void Reset() => Entries.Clear();
	}
}
