using System;
using System.Collections.Generic;
using System.Linq;
using Sevo.Framework.Sdk;

internal static class FunctionalLifetimeTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string failure)
        {
            if (!condition) throw new Exception(failure);
            checks++;
        }

        var placement = new List<string>();
        var errors = new List<Exception>();
        var lifetime = new FunctionalBlockLifetime<List<string>>(placement, log =>
        {
            log.Add("factory");
            return new Recorder(log);
        }, errors.Add);
        lifetime.SetSustainedInput(true);
        Check(placement.Count == 0, "Input before start created or activated a behavior.");
        lifetime.Start(fromLoad: true);
        lifetime.Start(fromLoad: true);
        lifetime.SetSustainedInput(false);
        Check(placement.SequenceEqual(new[] { "factory", "spawn", "load" }),
            "Loading must spawn once, notify load once, and leave the input off.");

        var otherPlacement = new List<string>();
        var other = new FunctionalBlockLifetime<List<string>>(otherPlacement, log => new Recorder(log), errors.Add);
        other.Start(fromLoad: false);
        lifetime.SetSustainedInput(true);
        lifetime.SetSustainedInput(true);
        lifetime.SetSustainedInput(false);
        lifetime.SetSustainedInput(false);
        lifetime.SetSustainedInput(true);
        lifetime.Stop();
        lifetime.Stop();
        lifetime.SetSustainedInput(true);
        lifetime.Start(fromLoad: false);
        Check(placement.SequenceEqual(new[] { "factory", "spawn", "load", "on", "off", "on", "off", "despawn" }),
            "Input must report only edges, and stopping must release active input before one despawn.");
        other.Stop();
        Check(otherPlacement.SequenceEqual(new[] { "spawn", "despawn" }) && errors.Count == 0,
            "Fresh placements must remain independent and receive neither load nor redundant off callbacks.");

        var factoryError = new InvalidOperationException("factory failure");
        var failingFactory = new FunctionalBlockLifetime<object>(new object(), _ => throw factoryError, errors.Add);
        failingFactory.Start(fromLoad: true);
        failingFactory.Start(fromLoad: true);
        failingFactory.SetSustainedInput(true);
        failingFactory.Stop();
        Check(errors.SequenceEqual(new[] { factoryError }), "A failing factory must be reported once and must never retry.");

        var loadLog = new List<string>();
        var loadError = new InvalidOperationException("load failure");
        var failingLoad = new FunctionalBlockLifetime<List<string>>(loadLog,
            log => new Recorder(log, step => { if (step == "load") throw loadError; }), errors.Add);
        failingLoad.Start(fromLoad: true);
        failingLoad.SetSustainedInput(true);
        failingLoad.Stop();
        Check(loadLog.SequenceEqual(new[] { "spawn", "load", "despawn" }) && ReferenceEquals(errors[^1], loadError),
            "A failed load must report its error, despawn once, and block later input.");

        var callbackLog = new List<string>();
        var callbackErrors = new List<Exception>();
        var failingInput = new FunctionalBlockLifetime<List<string>>(callbackLog,
            log => new Recorder(log, step =>
            {
                if (step != "spawn") throw new InvalidOperationException(step);
            }), exception =>
            {
                callbackErrors.Add(exception);
                throw new InvalidOperationException("report failure");
            });
        failingInput.Start(fromLoad: false);
        failingInput.SetSustainedInput(true);
        failingInput.SetSustainedInput(true);
        failingInput.Stop();
        Check(callbackLog.SequenceEqual(new[] { "spawn", "on", "off", "despawn" }) &&
              callbackErrors.Select(error => error.Message).SequenceEqual(new[] { "on", "off", "despawn" }),
            "Callback and reporter failures must leave one best-effort release and despawn without escaping.");

        var reentrantLog = new List<string>();
        FunctionalBlockLifetime<List<string>> reentrant = null!;
        reentrant = new FunctionalBlockLifetime<List<string>>(reentrantLog,
            log => new Recorder(log, step =>
            {
                if (step == "on")
                {
                    reentrant.Stop();
                    log.Add("on returned");
                }
                if (step == "despawn") reentrant.Stop();
            }), errors.Add);
        reentrant.Start(fromLoad: false);
        reentrant.SetSustainedInput(true);
        reentrant.Stop();
        Check(reentrantLog.SequenceEqual(new[] { "spawn", "on", "on returned", "off", "despawn" }),
            "A stop requested from a callback must clean up after that callback and stay idempotent.");

        var unlinkLog = new List<string>();
        FunctionalBlockLifetime<List<string>> unlinking = null!;
        unlinking = new FunctionalBlockLifetime<List<string>>(unlinkLog,
            log => new Recorder(log, step =>
            {
                if (step != "on") return;
                unlinking.SetSustainedInput(false);
                log.Add("on returned");
            }), errors.Add);
        unlinking.Start(fromLoad: false);
        unlinking.SetSustainedInput(true);
        Check(unlinkLog.SequenceEqual(new[] { "spawn", "on", "on returned", "off" }),
            "An unlink during activation must release input as soon as the active callback returns.");
        unlinking.Stop();

        return checks;
    }

    private sealed class Recorder(List<string> log, Action<string>? after = null) : FunctionalBlockBehavior
    {
        public override void OnSpawn() => Record("spawn");
        public override void OnLoad() => Record("load");
        public override void OnSustainedInput(bool active) => Record(active ? "on" : "off");
        public override void OnDespawn() => Record("despawn");

        private void Record(string step)
        {
            log.Add(step);
            after?.Invoke(step);
        }
    }
}
