using DemoSync.Api.Models;

namespace DemoSync.Api.Services;

public interface ISessionRepository
{
    void Add(ProcessingSession session);

    /// <summary>Returns a point-in-time copy of the session, or null when unknown.</summary>
    ProcessingSession? Get(Guid id);

    /// <summary>Applies a mutation atomically and returns a snapshot of the result, or null when unknown.</summary>
    ProcessingSession? Update(Guid id, Action<ProcessingSession> mutate);
}

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ProcessingSession> _sessions = new();

    public void Add(ProcessingSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!_sessions.TryAdd(session.Id, session))
        {
            throw new InvalidOperationException($"Session {session.Id} already exists.");
        }
    }

    public ProcessingSession? Get(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        lock (session)
        {
            return session.Clone();
        }
    }

    public ProcessingSession? Update(Guid id, Action<ProcessingSession> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        lock (session)
        {
            mutate(session);
            return session.Clone();
        }
    }
}
