using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MusicSyncConverter
{
    internal class MemoryLogger : ILogger
    {
        private readonly ConcurrentBag<(LogLevel LogLevel, string Message)> _logMessages = new();
        public IList<(LogLevel LogLevel, string Message)> Messages => _logMessages.ToList();

        public IDisposable? BeginScope<TState>(TState state)
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            _logMessages.Add((logLevel, formatter(state, exception)));
        }
    }
}
