using Microsoft.Extensions.Logging;
using MusicSyncConverter.FileProviders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace MusicSyncConverter
{
    internal class MemoryLogger : ILogger
    {
        private readonly ConcurrentBag<(LogLevel LogLevel, string? Filename, string Message)> _logMessages = new();
        private readonly ScopeProvider _scopeProvider;

        public IList<(LogLevel LogLevel, string? Filename, string Message)> Messages => _logMessages.ToList();
        public MemoryLogger()
        {
            _scopeProvider = new ScopeProvider();
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return _scopeProvider.Push(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var states = _scopeProvider.GetStates().OfType<IDictionary<string, object?>>().SelectMany(x => x).ToList();

            var sourceFile = states.FirstOrDefault(x => x.Key == "SourceFile").Value?.ToString();
            var targetFile = states.FirstOrDefault(x => x.Key == "TargetFile").Value?.ToString();

            var message = new StringBuilder();
            message.Append(formatter(state, exception));
            if(exception != null)
            {
                message.AppendLine(":");
                message.AppendLine(exception.ToString());
            }

            _logMessages.Add((logLevel, PathUtils.NormalizePath(targetFile ?? sourceFile), message.ToString()));
        }

        private class ScopeProvider
        {
            private readonly AsyncLocal<Scope?> _currentScope = new();

            public IEnumerable<object> GetStates()
            {
                var scope = _currentScope.Value;
                while (scope != null)
                {
                    if (scope.State != null)
                        yield return scope.State;
                    scope = scope.Parent;
                }
            }

            public IDisposable Push(object? state)
            {
                Scope? parent = _currentScope.Value;
                var newScope = new Scope(this, state, parent);
                _currentScope.Value = newScope;

                return newScope;
            }

            private class Scope : IDisposable
            {
                private readonly ScopeProvider _provider;
                private bool _isDisposed;

                public Scope(ScopeProvider provider, object? state, Scope? parent)
                {
                    _provider = provider;
                    State = state;
                    Parent = parent;
                }

                public object? State { get; }
                public Scope? Parent { get; }

                public void Dispose()
                {
                    if (!_isDisposed)
                    {
                        _provider._currentScope.Value = Parent;
                        _isDisposed = true;
                    }
                }
            }
        }
    }
}
