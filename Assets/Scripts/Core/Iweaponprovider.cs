namespace StealthHuntAI
{
    /// <summary>
    /// Interface for weapon components attached to StealthHuntAI units.
    /// Allows Core to query weapon range without depending on Demo assembly.
    ///
    /// Implement this on your own weapon script to integrate with StealthHuntAI.
    /// Example: public class MyWeapon : MonoBehaviour, IWeaponProvider { ... }
    /// </summary>
    public interface IWeaponProvider
    {
        /// <summary>Maximum effective shooting range in world units.</summary>
        float ShootRange { get; }

        /// <summary>True when the weapon is ready to fire.</summary>
        bool IsReady { get; }
    }
}