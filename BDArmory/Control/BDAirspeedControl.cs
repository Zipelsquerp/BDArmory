﻿using System.Collections.Generic;
using UnityEngine;
using BDArmory.Modules;

namespace BDArmory.Control
{
    public class BDAirspeedControl : MonoBehaviour //: PartModule
    {
        //[KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "TargetSpeed"),
        //	UI_FloatRange(minValue = 1f, maxValue = 420f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float targetSpeed = 0;
        public float throttleOverride = -1f;
        public bool useBrakes = true;
        public bool allowAfterburner = true;
        double afterburnerCoolDownPeriod = 0.5; // Cool-down to prevent rapid toggling of the afterburner when the hysteresis is insufficient.
        double afterburnerLastToggled = -1;

        //[KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "ThrottleFactor"),
        //	UI_FloatRange(minValue = 1f, maxValue = 20f, stepIncrement = .5f, scene = UI_Scene.All)]
        public float throttleFactor = 2f;

        public Vessel vessel;

        bool controlEnabled;

        //[KSPField(guiActive = true, guiName = "Thrust")]
        public float debugThrust;

        public List<MultiModeEngine> multiModeEngines;

        //[KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "ToggleAC")]
        public void Toggle()
        {
            if (controlEnabled)
            {
                Deactivate();
            }
            else
            {
                Activate();
            }
        }

        public void Activate()
        {
            controlEnabled = true;
            vessel.OnFlyByWire -= AirspeedControl;
            vessel.OnFlyByWire += AirspeedControl;
            multiModeEngines = new List<MultiModeEngine>();
        }

        public void Deactivate()
        {
            controlEnabled = false;
            vessel.OnFlyByWire -= AirspeedControl;
        }

        void AirspeedControl(FlightCtrlState s)
        {
            if (targetSpeed == 0)
            {
                if (useBrakes)
                    vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
                s.mainThrottle = 0;
                return;
            }

            float currentSpeed = (float)vessel.srfSpeed;
            float speedError = targetSpeed - currentSpeed;

            float setAccel = speedError * throttleFactor;

            SetAcceleration(setAccel, s);
        }

        void SetAcceleration(float accel, FlightCtrlState s)
        {
            float gravAccel = GravAccel();
            float requestEngineAccel = accel - gravAccel;

            possibleAccel = 0; //gravAccel;

            float dragAccel = 0;
            float engineAccel = MaxEngineAccel(requestEngineAccel, out dragAccel);

            if (throttleOverride >= 0)
            {
                s.mainThrottle = throttleOverride;
                return;
            }
            if (engineAccel == 0)
            {
                s.mainThrottle = accel > 0 ? 1 : 0;
                return;
            }

            requestEngineAccel = Mathf.Clamp(requestEngineAccel, -engineAccel, engineAccel);

            float requestThrottle = (requestEngineAccel - dragAccel) / engineAccel;

            s.mainThrottle = Mathf.Clamp01(requestThrottle);

            //use brakes if overspeeding too much
            if (useBrakes)
            {
                if (requestThrottle < -0.5f)
                {
                    vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
                }
                else
                {
                    vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);
                }
            }
        }

        float MaxEngineAccel(float requestAccel, out float dragAccel)
        {
            float maxThrust = 0;
            float finalThrust = 0;
            multiModeEngines.Clear();

            using (var engines = VesselModuleRegistry.GetModules<ModuleEngines>(vessel).GetEnumerator())
                while (engines.MoveNext())
                {
                    if (engines.Current == null) continue;
                    if (!engines.Current.EngineIgnited) continue;

                    MultiModeEngine mme = engines.Current.part.FindModuleImplementing<MultiModeEngine>();
                    if (IsAfterBurnerEngine(mme))
                    {
                        multiModeEngines.Add(mme);
                        mme.autoSwitch = false;
                    }

                    if (mme && mme.mode != engines.Current.engineID) continue;
                    float engineThrust = engines.Current.maxThrust;
                    if (engines.Current.atmChangeFlow)
                    {
                        engineThrust *= engines.Current.flowMultiplier;
                    }
                    maxThrust += Mathf.Max(0f, engineThrust * (engines.Current.thrustPercentage / 100f)); // Don't include negative thrust percentage drives (Danny2462 drives) as they don't contribute to the thrust.

                    finalThrust += engines.Current.finalThrust;
                }

            debugThrust = maxThrust;

            float vesselMass = vessel.GetTotalMass();

            float accel = maxThrust / vesselMass; // This assumes that all thrust is in the same direction.

            //estimate drag
            float estimatedCurrentAccel = finalThrust / vesselMass - GravAccel();
            Vector3 vesselAccelProjected = Vector3.Project(vessel.acceleration_immediate, vessel.velocityD.normalized);
            float actualCurrentAccel = vesselAccelProjected.magnitude * Mathf.Sign(Vector3.Dot(vesselAccelProjected, vessel.velocityD.normalized));
            float accelError = (actualCurrentAccel - estimatedCurrentAccel); // /2 -- why divide by 2 here?
            dragAccel = accelError;

            possibleAccel += accel; // This assumes that the acceleration from engines is in the same direction as the original possibleAccel.

            //use multimode afterburner for extra accel if lacking
            using (List<MultiModeEngine>.Enumerator mmes = multiModeEngines.GetEnumerator())
                while (mmes.MoveNext())
                {
                    if (mmes.Current == null) continue;
                    if (allowAfterburner && accel < requestAccel * 0.2f && Time.time - afterburnerLastToggled > afterburnerCoolDownPeriod)
                    {
                        if (mmes.Current.runningPrimary)
                        {
                            mmes.Current.Events["ModeEvent"].Invoke();
                            afterburnerLastToggled = Time.time;
                        }
                    }
                    else if (!allowAfterburner || accel > requestAccel * 1.5f && Time.time - afterburnerLastToggled > afterburnerCoolDownPeriod)
                    {
                        if (!mmes.Current.runningPrimary)
                        {
                            mmes.Current.Events["ModeEvent"].Invoke();
                            afterburnerLastToggled = Time.time;
                        }
                    }
                }
            return accel;
        }

        private static bool IsAfterBurnerEngine(MultiModeEngine engine)
        {
            if (engine == null)
            {
                return false;
            }
            if (!engine)
            {
                return false;
            }
            return engine.primaryEngineID == "Dry" && engine.secondaryEngineID == "Wet";
        }

        float GravAccel()
        {
            Vector3 geeVector = FlightGlobals.getGeeForceAtPosition(vessel.CoM);
            float gravAccel = geeVector.magnitude * Mathf.Cos(Mathf.Deg2Rad * Vector3.Angle(-geeVector, vessel.velocityD)); // -g.v/|v| ???
            return gravAccel;
        }

        float possibleAccel;

        public float GetPossibleAccel()
        {
            return possibleAccel;
        }
    }

    public class BDLandSpeedControl : MonoBehaviour
    {
        public float targetSpeed;
        public Vessel vessel;
        public bool preventNegativeZeroPoint = false;

        private float lastThrottle;
        public float zeroPoint { get; private set; }

        private const float gain = 0.5f;
        private const float zeroMult = 0.02f;

        public void Activate()
        {
            vessel.OnFlyByWire -= SpeedControl;
            vessel.OnFlyByWire += SpeedControl;
            zeroPoint = 0;
            lastThrottle = 0;
        }

        public void Deactivate()
        {
            vessel.OnFlyByWire -= SpeedControl;
        }

        void SpeedControl(FlightCtrlState s)
        {
            if (!vessel.LandedOrSplashed)
                s.wheelThrottle = 0;
            else if (targetSpeed == 0)
            {
                vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
                s.wheelThrottle = 0;
            }
            else
            {
                float throttle = zeroPoint + (targetSpeed - (float)vessel.srfSpeed) * gain;
                lastThrottle = Mathf.Clamp(throttle, -1, 1);
                zeroPoint = (zeroPoint + lastThrottle * zeroMult) * (1 - zeroMult);
                if (preventNegativeZeroPoint && zeroPoint < 0) zeroPoint = 0;
                s.wheelThrottle = lastThrottle;
                vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, throttle < -5f);
            }
        }
    }
}
