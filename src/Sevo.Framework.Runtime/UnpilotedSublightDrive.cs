using System;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal sealed class UnpilotedSublightDrive
    {
        private static Bindings? bindings;
        private readonly Spaceship ship;
        private readonly EntityRigidbody body;
        private readonly ComputerSpaceship core;
        private readonly SpaceshipDrive drive;
        private readonly int bodyId;

        internal UnpilotedSublightDrive(Spaceship ship, EntityRigidbody body, ComputerSpaceship core)
        {
            this.ship = ship;
            this.body = body;
            this.core = core;
            bodyId = body.OwnSkyRigidbody.id;
            // This drive holds its own controller history and never becomes the ship's native drive.
            drive = new SpaceshipDrive(ship, body);
        }

        internal static bool TryBind(Action<string> warn)
        {
            try
            {
                bindings = new Bindings();
                return true;
            }
            catch (Exception exception)
            {
                bindings = null;
                warn("Unpiloted sublight binding failed: " + exception.GetBaseException().Message);
                return false;
            }
        }

        internal bool TryApply(Spaceship candidate, ComputerSpaceship computer, Vector3 translation, Vector3 rotation)
        {
            if (bindings is not { } native || !ReferenceEquals(candidate, ship) || !ReferenceEquals(computer, core)
                || !ReferenceEquals(ship.entity.Rigidbody, body) || body.OwnSkyRigidbody.id != bodyId
                || !float.IsFinite(body.Weight) || body.Weight <= 0f || !float.IsFinite(body.inertia.sqrMagnitude))
                return false;
            var bodyData = body.OwnSkyRigidbody.Data;
            var worldOrientation = ship.transform.rotation * core.inputOrientation;
            float orientationLength = Quaternion.Dot(worldOrientation, worldOrientation);
            if (!(Mathf.Abs(orientationLength - 1f) <= 0.01f)
                || !float.IsFinite(((Vector3)bodyData.linearVelocity).sqrMagnitude)
                || !float.IsFinite(((Vector3)bodyData.angularVelocity).sqrMagnitude))
                return false;

            var systems = ship.entity.Systems;
            ref Quaternion orientation = ref native.Orientation(ship);
            ref float thrustForward = ref native.ThrustForward(systems);
            ref float thrustLateral = ref native.ThrustLateral(systems);
            var savedOrientation = orientation;
            float savedForward = thrustForward, savedLateral = thrustLateral;
            var savedCore = ship.computerBaseVehicle;
            try
            {
                orientation = core.inputOrientation;
                ship.computerBaseVehicle = core;
                systems.ComputeThrust(orientation * Vector3.forward, useRepulsors: false, updateThrusters: false);
                if (!float.IsFinite(thrustForward) || !float.IsFinite(thrustLateral)
                    || thrustForward < 0f || thrustLateral < 0f || (thrustForward == 0f && thrustLateral == 0f)
                    || !float.IsFinite(core.MaximumVelocity.sqrMagnitude) || !float.IsFinite(core.AngularSpeed))
                    return false;

                native.DriveOrientation(drive) = worldOrientation;
                native.ApplyForce(drive, translation);
                native.ApplyTorque(drive, rotation);
                return true;
            }
            finally
            {
                ship.computerBaseVehicle = savedCore;
                orientation = savedOrientation;
                thrustForward = savedForward;
                thrustLateral = savedLateral;
            }
        }

        private sealed class Bindings
        {
            internal readonly AccessTools.FieldRef<BaseVehicle, Quaternion> Orientation =
                AccessTools.FieldRefAccess<BaseVehicle, Quaternion>("<Orientation>k__BackingField");
            internal readonly AccessTools.FieldRef<EntitySystems, float> ThrustForward =
                AccessTools.FieldRefAccess<EntitySystems, float>("<ThrustForward>k__BackingField");
            internal readonly AccessTools.FieldRef<EntitySystems, float> ThrustLateral =
                AccessTools.FieldRefAccess<EntitySystems, float>("<ThrustLateral>k__BackingField");
            internal readonly AccessTools.FieldRef<SpaceshipDrive, Quaternion> DriveOrientation =
                AccessTools.FieldRefAccess<SpaceshipDrive, Quaternion>("q");
            internal readonly Action<SpaceshipDrive, Vector3> ApplyForce = BindDriveMethod("ComputeAndApplyForce");
            internal readonly Action<SpaceshipDrive, Vector3> ApplyTorque = BindDriveMethod("ComputeAndApplyTorque");

            private static Action<SpaceshipDrive, Vector3> BindDriveMethod(string name)
            {
                var method = AccessTools.DeclaredMethod(typeof(SpaceshipDrive), name, new[] { typeof(Vector3) });
                if (method == null || !method.IsPrivate || method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException(typeof(SpaceshipDrive).FullName, name);
                return (Action<SpaceshipDrive, Vector3>)method.CreateDelegate(typeof(Action<SpaceshipDrive, Vector3>));
            }
        }
    }
}
