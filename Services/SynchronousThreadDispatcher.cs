using System.Runtime.ExceptionServices;

namespace MidFD.Services;

internal static class SynchronousThreadDispatcher
{
    public static T Invoke<T>(Action<Action>? dispatcher, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher == null)
        {
            return action();
        }

        T? result = default;
        ExceptionDispatchInfo? failure = null;
        bool invoked = false;
        dispatcher(() =>
        {
            if (invoked)
            {
                throw new InvalidOperationException("The owner-thread dispatcher invoked the action more than once.");
            }

            invoked = true;
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        if (!invoked)
        {
            throw new InvalidOperationException("The owner-thread dispatcher must invoke the action synchronously.");
        }

        failure?.Throw();
        return result!;
    }

    public static void Invoke(Action<Action>? dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Invoke(dispatcher, () =>
        {
            action();
            return true;
        });
    }
}
