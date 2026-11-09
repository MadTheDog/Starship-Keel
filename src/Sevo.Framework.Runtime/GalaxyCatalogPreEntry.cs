using System;
using System.IO;

namespace Sevo.Framework.Runtime
{
    /// <summary>Validates a native character selection without opening or writing it.</summary>
    internal static class GalaxyCatalogPreEntry
    {
        internal static GalaxyCatalogProfilePin VerifySelection(ProfileSession profile, string nativeSaveRoot, GalaxyCatalogProfilePin? pin,
            string providerKey, int revision, string fingerprint, string characterName, ulong worldSeed, bool creating, string? selectedSavePath = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            GalaxyCatalogProfilePin.RequireSafeName(characterName);
            string root = GalaxyCatalogNativeSaveBinding.Verify(profile, nativeSaveRoot, requireEmpty: pin == null);
            if (pin != null && (!pin.BelongsTo(profile.ProfileId) || !pin.Matches(root, providerKey, revision, fingerprint)))
                throw new InvalidOperationException("The selected profile requires its original galaxy catalog and native save root.");
            string directory = Path.Combine(root, characterName);
            string save = Path.Combine(directory, characterName + ".mpc");
            if (creating)
            {
                if (selectedSavePath != null || Directory.Exists(directory) || File.Exists(directory))
                    throw new InvalidOperationException("The new character path already exists in the native save root.");
                var current = pin ?? new GalaxyCatalogProfilePin(profile.ProfileId, root, providerKey, revision, fingerprint);
                return current.WithWorld(characterName, worldSeed);
            }

            if (pin == null || !pin.ContainsWorld(characterName, worldSeed)
                || selectedSavePath == null || !string.Equals(save, Path.GetFullPath(selectedSavePath), StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(directory) || !File.Exists(save)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(save) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The selected character is not an exact native world admitted by this galaxy catalog.");
            return pin;
        }
    }
}
