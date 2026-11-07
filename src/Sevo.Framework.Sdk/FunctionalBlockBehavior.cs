namespace Sevo.Framework.Sdk
{
    /// <summary>Callbacks for one live placement of a functional block.</summary>
    public abstract class FunctionalBlockBehavior
    {
        /// <summary>Called when the placement begins its live lifetime.</summary>
        public virtual void OnSpawn() { }

        /// <summary>Called after spawn when this placement was loaded.</summary>
        public virtual void OnLoad() { }

        /// <summary>Called when the requested sustained input changes. Input begins off.</summary>
        public virtual void OnSustainedInput(bool active) { }

        /// <summary>Called when this behavior is released, including after a callback fails.</summary>
        public virtual void OnDespawn() { }
    }
}
