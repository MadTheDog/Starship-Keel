using World;

namespace Sevo.Framework.Runtime
{
    internal static class LoadedRockyBody
    {
        internal static SolarBody? Find(ulong star, ulong body)
        {
            var bodies = PlanetarySystem.SolarBodies;
            if (PlanetarySystem.InSystemTransition || bodies == null || PlanetarySystem.CurrentStarData.seed != star)
                return null;
            SolarBody? selected = null;
            foreach (var candidate in bodies)
            {
                if (candidate == null || candidate.AstronType != AstronType.telluric
                    || candidate.parentStar.seed != star || candidate.Seed != body)
                    continue;
                if (selected != null) return null;
                selected = candidate;
            }
            return selected;
        }
    }
}
