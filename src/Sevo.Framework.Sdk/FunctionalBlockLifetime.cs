using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Sevo.Framework.Runtime")]
[assembly: InternalsVisibleTo("Sevo.Framework.Tests")]

namespace Sevo.Framework.Sdk
{
    // The runtime dispatches each placement's lifetime on its game thread.
    internal sealed class FunctionalBlockLifetime<TContext> where TContext : class
    {
        private enum Stage { Waiting, Starting, Running, Stopped }

        private readonly TContext context;
        private readonly Func<TContext, FunctionalBlockBehavior> factory;
        private readonly Action<Exception> report;
        private FunctionalBlockBehavior? behavior;
        private Stage stage;
        private bool active;
        private bool requestedActive;
        private bool dispatching;

        internal FunctionalBlockLifetime(TContext context, Func<TContext, FunctionalBlockBehavior> factory,
            Action<Exception> report)
        {
            this.context = context;
            this.factory = factory;
            this.report = report;
        }

        internal void Start(bool fromLoad)
        {
            if (stage != Stage.Waiting) return;
            stage = Stage.Starting;
            Dispatch(() =>
            {
                behavior = factory(context) ?? throw new InvalidOperationException("The functional block factory returned no behavior.");
                if (stage == Stage.Stopped) return;
                behavior.OnSpawn();
                if (stage == Stage.Stopped) return;
                if (fromLoad) behavior.OnLoad();
                if (stage != Stage.Stopped) stage = Stage.Running;
            });
        }

        internal void SetSustainedInput(bool value)
        {
            if (stage != Stage.Running) return;
            requestedActive = value;
            if (dispatching || active == value) return;

            Dispatch(() =>
            {
                // A callback can change its native links. Deliver the resulting level after it returns.
                while (stage == Stage.Running && active != requestedActive)
                {
                    active = requestedActive;
                    behavior!.OnSustainedInput(active);
                }
            });
        }

        internal void Stop()
        {
            if (stage == Stage.Stopped) return;
            stage = Stage.Stopped;
            if (!dispatching) Cleanup();
        }

        private void Dispatch(Action callback)
        {
            dispatching = true;
            try { callback(); }
            catch (Exception exception)
            {
                stage = Stage.Stopped;
                Report(exception);
            }
            finally
            {
                dispatching = false;
                if (stage == Stage.Stopped) Cleanup();
            }
        }

        private void Cleanup()
        {
            var released = behavior;
            behavior = null;
            if (released == null) return;

            if (active)
            {
                active = false;
                try { released.OnSustainedInput(false); }
                catch (Exception exception) { Report(exception); }
            }
            try { released.OnDespawn(); }
            catch (Exception exception) { Report(exception); }
        }

        private void Report(Exception exception)
        {
            try { report(exception); }
            catch (Exception) { /* A diagnostics failure must not interrupt native dispatch or cleanup. */ }
        }
    }
}
