using System.Text;
using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Timers;
using Offsets;
using Vmmsharp;
using System.Diagnostics;
using static Vmmsharp.VmmProcess;
using System.Net;
using OpenTK.Graphics.ES10;

namespace eft_dma_radar
{      
    public class Aimbot
    {
        private Config _config;  // Declare _config
        float aimbotFOV;
        private float       aimbotMaxDistance;
        private bool        aimbotClosest;
        private bool        aimbotHead;
        private bool        aimbotNeck;
        private bool        aimbotChest;
        private bool        aimbotPelvis;
        private bool        aimbotRightLeg;
        private bool        aimbotLeftLeg;
        private int      aimbotKey;
        private int      saAimbotKey;
        private float      aimbotPing;
        private bool        aimbotEnabled;
        private bool        saimbotEnabled;
        private float aimbotPredictionDelay;
        private CameraManager _cameraManager { get => Memory.CameraManager; }
        private ReadOnlyDictionary<string, Player> AllPlayers { get => Memory.Players; }
        private bool InGame { get => Memory.InGame; }
        private static PlayerManager playamanaga { get => Memory.PlayerManager; }
        private Player LocalPlayer { get => Memory.LocalPlayer; }
        public ulong MovementContext { get; set; }
        private bool boneCache = false;
        public Dictionary<PlayerBones, Transform> boneTransforms = new Dictionary<PlayerBones, Transform>();
        private int boneCounter;
        private ScatterReadMap boneScatterMap;
        public ulong boneMatrix;
        public List<ulong> BonePointers { get; } = new List<ulong>();        
        private DateTime lastBoneReadTime = DateTime.MinValue;
        private Player TargetPlayer;
        private readonly Stopwatch executionTimer = new Stopwatch();   
        private readonly Vector2 screenCenter = new Vector2(1920 / 2f, 1080 / 2f);     
        public Aimbot()
        {            
            _config = Program.Config;
        }
        public void Initialize()
        {
            aimbotMaxDistance = _config.AimbotMaxDistance;
            aimbotFOV = _config.AimbotFOV;
            aimbotClosest = _config.AimbotClosest;
            aimbotHead = _config.AimbotHead;
            aimbotNeck = _config.AimbotNeck;
            aimbotChest = _config.AimbotChest;
            aimbotPelvis = _config.AimbotPelvis;
            aimbotRightLeg = _config.AimbotRightLeg;
            aimbotLeftLeg = _config.AimbotLeftLeg;
            aimbotKey = _config.AimbotKeybind;
            saAimbotKey = _config.SASilentAimKey;
            aimbotPing = _config.AimbotPing;
            aimbotPredictionDelay = _config.AimbotPredictionDelay;
            aimbotEnabled = _config.EnableAimbot;
            saimbotEnabled = _config.SAEnableAimbot;
            Execute();
        }      
        public void Execute()
        {
            if (!aimbotEnabled && !saimbotEnabled || !InGame || LocalPlayer == null || !LocalPlayer.IsAlive)
            {
                return;
            }

            if (executionTimer.IsRunning && executionTimer.ElapsedMilliseconds < 16)
                return;

            executionTimer.Restart();

            if (aimbotEnabled || saimbotEnabled)
            {
                ExecuteAimbot();
            }
        }
        public void ExecuteAimbot()
        {
            bool aimbotHeld = InputManager.IsKeyDown((Keys)aimbotKey);
            bool saAimbotHeld = InputManager.IsKeyDown((Keys)saAimbotKey);

            // Ensure the player is in-game and alive
            if (!InGame || LocalPlayer == null || !LocalPlayer.IsAlive)
            {
                Program.Log("Player is not in-game or invalid.");
                return;
            }

            var now = DateTime.Now;

            // Ensure a minimum delay between executions (16ms for ~60Hz refresh)
            if ((now - lastBoneReadTime).TotalMilliseconds < 16)
            {
                return;
            }

            if (aimbotHeld || saAimbotHeld)
            {
                Memory.CameraManager.UpdateViewMatrix();
                TargetPlayer = GetTargetPlayerOptimized();

                // Validate the target player
                if (TargetPlayer == null || !TargetPlayer.IsAlive)
                {
                    //Program.Log("No valid target found or target is dead.");
                    return;
                }

                // Read positions and velocities
                var (fireportPosition, targetBonePosition) = ReadAllBonePositions(TargetPlayer);

                // Validate positions
                if (fireportPosition == Vector3.Zero || targetBonePosition == Vector3.Zero)
                {
                    //Program.Log("Failed to retrieve valid positions for target.");
                    return;
                }
                var Velocity = TargetPlayer.Velocity;
                //var Velocity = Vector3.Zero;
                Vector3 predictedPosition = PredictTargetPosition(targetBonePosition, Velocity, TargetPlayer);
                //Program.Log($"Velocity: {Velocity}, Predicted Position: {predictedPosition}, TargetBonePosition: {targetBonePosition}");
                // Execute normal aimbot logic
                if(aimbotEnabled)
                {
                    if (aimbotHeld)
                    {
                        Vector2 aimAngle = CalcAngle(fireportPosition, predictedPosition);
                        NormalizeAngle(ref aimAngle);
                        LocalPlayer.SetRotationFr(aimAngle);
                        //Program.Log($"Aimbot: Predicted Position {predictedPosition}, AimAngle {aimAngle}");
                    }
                }
                if(saimbotEnabled)
                {
                    // Execute silent aim logic
                    if (saAimbotHeld)
                    {
                        SilentAim.ApplySilentAim(fireportPosition, predictedPosition);
                        //Program.Log($"SilentAim: Predicted Position {predictedPosition}");
                    }
                }

                // Reset bone cache and update last execution time
                ResetBoneCache();
                lastBoneReadTime = DateTime.Now;
            }
        }  
        private Vector3 PredictTargetPosition(Vector3 targetPosition, Vector3 targetVelocity, Player targetPlayer)
        {
            // Ignore minor vertical movements if X and Z velocities are near zero
            if (Math.Abs(targetVelocity.X) < 0.01f && Math.Abs(targetVelocity.Z) < 0.01f)
            {
                targetVelocity.Y = 0f;
            }

            // Calculate distance to the target
            var localBone = new Vector3(this.LocalPlayer.Position.X, this.LocalPlayer.Position.Z, this.LocalPlayer.Position.Y);
            float distanceToTarget = Vector3.Distance(localBone, targetPosition);

            // Adjust trajectory only if the target is beyond 60 meters
            if (distanceToTarget > 60f)
            {
                var angle = (localBone.Y - targetPosition.Y) / distanceToTarget;

                // Calculate bullet drop
                var drop = AimbotHelpers.FormTrajectory2(
                    distanceToTarget,
                    Vector3.Zero,
                    new Vector3(LocalPlayer.bullet_speed, 0, 0),
                    LocalPlayer.bullet_mass,
                    LocalPlayer.bullet_diam,
                    LocalPlayer.ballistic_coeff,
                    out AimbotHelpers.GStruct267[] trajectoryInfo2
                );

                var dropAdjusted = drop * (float)Math.Sin(angle);
                targetPosition.Y += drop - Math.Abs(dropAdjusted / 2);
            }
            if (distanceToTarget < 30f)
            {
                // If the target is closer than 60 meters, adjust the Y position by lowering it 20 cm (0.2 meters)
                targetPosition.Y -= 0.4f;
            }

            // Calculate the time of flight
            var speed = AimbotHelpers.OptimizedFormTrajectory(
                distanceToTarget,
                Vector3.Zero,
                new Vector3(LocalPlayer.bullet_speed, 0, 0),
                LocalPlayer.bullet_mass,
                LocalPlayer.bullet_diam,
                LocalPlayer.ballistic_coeff,
                out AimbotHelpers.GStruct267[] trajectoryInfo
            );

            float gamePing = (aimbotPing / 2000.0f);

            // Scale prediction based on `aimbotPredictionDelay`
            float delayScaling = Math.Clamp(aimbotPredictionDelay / 50f, 0f, 2f); // Normalize to a 0–2 scale
            float bulletFlightTime = (speed + gamePing) * delayScaling;

            // Predict the target's position
            targetPosition.X += targetVelocity.X * bulletFlightTime;
            targetPosition.Z += targetVelocity.Z * bulletFlightTime;

            if (distanceToTarget > 60f)
            {
                targetPosition.Y += targetVelocity.Y * bulletFlightTime;
            }

            return targetPosition;
        }        
        private void ResetBoneCache()
        {
            boneCache = false;
            boneTransforms.Clear();
            BonePointers.Clear();
        }
        private Player GetTargetPlayerOptimized()
        {
            if (!InputManager.IsKeyDown((Keys)aimbotKey) && !InputManager.IsKeyDown((Keys)saAimbotKey))
            {
                return null;  // Prevent execution unless key is pressed
            }

            var localPosition = LocalPlayer.Position;
            var screenCenter = new Vector2(1920 / 2f, 1080 / 2f);

            var validPlayers = AllPlayers.Values
                .AsParallel()
                .Where(p => p.IsActive && p.IsAlive && !p.IsLocalPlayer && Vector3.Distance(localPosition, p.Position) <= aimbotMaxDistance)
                .Select(p =>
                {
                    if (Extensions.WorldToScreen(p.Position, 1920, 1080, out Vector2 screenPos))
                    {
                        float screenDistance = Vector2.Distance(screenPos, screenCenter);
                        float worldDistance = Vector3.Distance(localPosition, p.Position);
                        return new { Player = p, ScreenDistance = screenDistance, WorldDistance = worldDistance };
                    }
                    return null;
                })
                .Where(result => result != null && result.ScreenDistance <= aimbotFOV);

            return aimbotClosest 
                ? validPlayers.OrderBy(result => result.WorldDistance).ThenBy(result => result.ScreenDistance).FirstOrDefault()?.Player 
                : validPlayers.OrderBy(result => result.ScreenDistance).FirstOrDefault()?.Player;
        }

        private static void NormalizeAngle(ref Vector2 angle)
        {
            var newX = angle.X switch
            {
                <= -180f => angle.X + 360f,
                > 180f => angle.X - 360f,
                _ => angle.X
            };

            var newY = angle.Y switch
            {
                > 90f => angle.Y - 180f,
                <= -90f => angle.Y + 180f,
                _ => angle.Y
            };

            angle = new Vector2(newX, newY);
        }  

        public static Vector2 CalcAngle(Vector3 source, Vector3 destination)
        {
            Vector3 difference = source - destination;
            float length = difference.Length();
            Vector2 ret = new Vector2();

            ret.Y = (float)Math.Asin(difference.Y / length);
            ret.X = -(float)Math.Atan2(difference.X, -difference.Z);
            ret = new Vector2(ret.X * 57.29578f, ret.Y * 57.29578f);

            return ret;
        }
        public class SilentAim
        {
            private const float Pi = 3.14159265358979323846f;

            private static float DegToRad(float degrees)
            {
                return degrees * (Pi / 180.0f);
            }

            private static float RadToDeg(float radians)
            {
                return radians * (180.0f / Pi);
            }

            public static Vector2 CalculateAngle(Vector3 from, Vector3 to)
            {
                Vector3 delta = from - to;
                float length = delta.Length();

                return new Vector2(
                    RadToDeg((float)-Math.Atan2(delta.X, -delta.Z)),
                    RadToDeg((float)Math.Asin(delta.Y / length))
                );
            }

            public static void ApplySilentAim(Vector3 fireportPos, Vector3 aimPos)
            {
                // Read current view angles
                Vector2 viewAngles = Memory.ReadValue<Vector2>(playamanaga._movementContext + 0x27C);

                // Calculate desired angle
                Vector2 angle = CalculateAngle(fireportPos, aimPos);

                // Normalize delta
                Vector2 delta = angle - viewAngles;
                delta = NormalizeAngle(delta);

                // Compute gun angle
                Vector3 gunAngle = new Vector3(
                    DegToRad(delta.X) / 1.5f,
                    0.0f,
                    DegToRad(delta.Y) / 1.5f
                );

                // Write the new gun angles to memory
                Memory.WriteValue(playamanaga._proceduralWeaponAnimation + 0x22C, new Vector3(gunAngle.X, -1.0f, gunAngle.Z * -1.0f));
            }

            private static Vector2 NormalizeAngle(Vector2 angle)
            {
                angle.X = NormalizeSingleAngle(angle.X);
                angle.Y = NormalizeSingleAngle(angle.Y);
                return angle;
            }

            private static float NormalizeSingleAngle(float angle)
            {
                while (angle > 180.0f) angle -= 360.0f;
                while (angle < -180.0f) angle += 360.0f;
                return angle;
            }
        }                        
        private List<PlayerBones> bones;
        public void InitializeBonesFromConfig()
        {
            bones = new List<PlayerBones>();

            if (_config.AimbotHead)
                bones.Add(PlayerBones.HumanHead);
            if (_config.AimbotNeck)
                bones.Add(PlayerBones.HumanNeck);
            if (_config.AimbotChest)
                bones.Add(PlayerBones.HumanSpine3);
            if (_config.AimbotPelvis)
                bones.Add(PlayerBones.HumanPelvis);
            if (_config.AimbotRightLeg)
                bones.Add(PlayerBones.HumanRCalf);
            if (_config.AimbotLeftLeg)
                bones.Add(PlayerBones.HumanLCalf);
        }               
        public (Vector3 FireportPosition, Vector3 TargetBonePosition) ReadAllBonePositions(Player player)
        {
            if (player == null || player.Name == "???" || !player.IsAlive)
            {
                //Program.Log("Skipping invalid or dead player. Clearing target.");
                TargetPlayer = null; // Clear the target
                return (Vector3.Zero, Vector3.Zero);
            }
            InitializeBonesFromConfig();
            // Reset bone cache
            boneCache = false;

            if (!boneCache)
            {
                boneCache = true;
                boneCounter = bones.Count + 3; // Include Fireport position and both velocities
                boneScatterMap = new ScatterReadMap(boneCounter);

                var baseReadRound = boneScatterMap.AddRound();
                var derefReadRound = boneScatterMap.AddRound();
                // Add scatter read for bones
                for (int i = 0; i < bones.Count; i++)
                {
                    var boneMatrix = Memory.ReadPtrChain(player.PlayerBody, new uint[] { 0x30, 0x30, 0x10 });
                    var basePtr = baseReadRound.AddEntry<MemPointer>(i, 0, boneMatrix, null, 0x20 + ((uint)bones[i] * 0x8));
                    derefReadRound.AddEntry<MemPointer>(i, 1, basePtr, null, 0x10);
                }
                // Add scatter read for Fireport
                var firearmControllerPtr = baseReadRound.AddEntry<MemPointer>(bones.Count, 0, playamanaga._proceduralWeaponAnimation, null, ProceduralWeaponAnimation.FirearmContoller);
                var fireportPtr = derefReadRound.AddEntry<MemPointer>(bones.Count, 1, firearmControllerPtr, null, FirearmController.Fireport);
                // Add scatter read for offline velocity
                var offlineCharacterControllerPtr = baseReadRound.AddEntry<MemPointer>(bones.Count + 1, 0, player.Base, null, 0x40);
                derefReadRound.AddEntry<Vector3>(bones.Count + 1, 1, offlineCharacterControllerPtr, null, 0xEC);
                // Add scatter read for online velocity
                derefReadRound.AddEntry<Vector3>(bones.Count + 2, 0, player.MovementContext, null, 0x10C);
                // Execute the scatter map reads
                boneScatterMap.Execute();
                // Populate bone transforms
                for (int i = 0; i < bones.Count; i++)
                {
                    if (boneScatterMap.Results[i][1].TryGetResult<MemPointer>(out var bonePointer))
                    {
                        BonePointers.Add(bonePointer);
                        if (!boneTransforms.ContainsKey(bones[i]))
                        {
                            boneTransforms[bones[i]] = new Transform(bonePointer, false);
                        }
                    }
                }
            }
            // Retrieve the Fireport position
            Vector3 fireportPosition = Vector3.Zero;
            if (boneScatterMap.Results[bones.Count][1].TryGetResult<MemPointer>(out var fireportPointer))
            {
                ulong handsContainer = Memory.ReadPtrChain(fireportPointer, new uint[] { Fireport.To_TransfromInternal[0], Fireport.To_TransfromInternal[1] });
                Transform fireportTransform = new Transform(handsContainer);
                fireportPosition = fireportTransform.GetPosition();
            }
            else
            {
                Program.Log("Failed to retrieve Fireport position.");
            }
            // Retrieve the target bone position (using the first bone in the list as an example)
            Vector3 targetBonePosition = Vector3.Zero;
            if (bones.Count > 0 && boneTransforms.ContainsKey(bones[0]))
            {
                Vector3 rawBonePosition = boneTransforms[bones[0]].GetPosition();
                targetBonePosition = rawBonePosition;
            }
            // Find the closest bone to the center of the screen
            Vector2 screenCenter = new Vector2(1920 / 2f, 1080 / 2f);
            Vector3 closestBonePosition = Vector3.Zero;
            float closestDistance = float.MaxValue;            
            foreach (var bone in bones)
            {
                if (boneScatterMap.Results[bones.IndexOf(bone)][1].TryGetResult<MemPointer>(out var bonePointer))
                {
                    Transform boneTransform = new Transform(bonePointer);
                    Vector3 bonePosition = boneTransform.GetPosition();

                    if (Extensions.WorldToScreen(bonePosition, 1920, 1080, out Vector2 screenPos))
                    {
                        float distanceToCenter = Vector2.Distance(screenPos, screenCenter);

                        if (distanceToCenter < closestDistance)
                        {
                            closestDistance = distanceToCenter;
                            closestBonePosition = bonePosition;
                        }
                    }
                }
            }
            return (fireportPosition, closestBonePosition);
        }
    }    
    public class AimbotHelpers
    {
        public struct GStruct267
        {
            public float time;
            public Vector3 position;
            public Vector3 velocity;

            public GStruct267(float time, Vector3 position, Vector3 velocity)
            {
                this.time = time;
                this.position = position;
                this.velocity = velocity;
            }
        }
        public static float CalculateG1DragCoefficient(float velocity)
        {
            int index = (int)Math.Floor(velocity / 343f / 0.05f);
            if (index <= 0)
            {
                return 0f;
            }
            if (index > speedlist.Count - 1)
            {
                return speedlist.Last<Vector2>().Y;
            }
            float prevVelocity = speedlist[index - 1].X * 343f;
            float nextVelocity = speedlist[index].X * 343f;
            float prevDrag = speedlist[index - 1].Y;
            return (speedlist[index].Y - prevDrag) / (nextVelocity - prevVelocity) * (velocity - prevVelocity) + prevDrag;
        }
        private static readonly List<Vector2> speedlist = new List<Vector2>
        {
            new Vector2(0f, 0.2629f),
            new Vector2(0.05f, 0.2558f),
            new Vector2(0.1f, 0.2487f),
            new Vector2(0.15f, 0.2413f),
            new Vector2(0.2f, 0.2344f),
            new Vector2(0.25f, 0.2278f),
            new Vector2(0.3f, 0.2214f),
            new Vector2(0.35f, 0.2155f),
            new Vector2(0.4f, 0.2104f),
            new Vector2(0.45f, 0.2061f),
            new Vector2(0.5f, 0.2032f),
            new Vector2(0.55f, 0.202f),
            new Vector2(0.6f, 0.2034f),
            new Vector2(0.7f, 0.2165f),
            new Vector2(0.725f, 0.223f),
            new Vector2(0.75f, 0.2313f),
            new Vector2(0.775f, 0.2417f),
            new Vector2(0.8f, 0.2546f),
            new Vector2(0.825f, 0.2706f),
            new Vector2(0.85f, 0.2901f),
            new Vector2(0.875f, 0.3136f),
            new Vector2(0.9f, 0.3415f),
            new Vector2(0.925f, 0.3734f),
            new Vector2(0.95f, 0.4084f),
            new Vector2(0.975f, 0.4448f),
            new Vector2(1f, 0.4805f),
            new Vector2(1.025f, 0.5136f),
            new Vector2(1.05f, 0.5427f),
            new Vector2(1.075f, 0.5677f),
            new Vector2(1.1f, 0.5883f),
            new Vector2(1.125f, 0.6053f),
            new Vector2(1.15f, 0.6191f),
            new Vector2(1.2f, 0.6393f),
            new Vector2(1.25f, 0.6518f),
            new Vector2(1.3f, 0.6589f),
            new Vector2(1.35f, 0.6621f),
            new Vector2(1.4f, 0.6625f),
            new Vector2(1.45f, 0.6607f),
            new Vector2(1.5f, 0.6573f),
            new Vector2(1.55f, 0.6528f),
            new Vector2(1.6f, 0.6474f),
            new Vector2(1.65f, 0.6413f),
            new Vector2(1.7f, 0.6347f),
            new Vector2(1.75f, 0.628f),
            new Vector2(1.8f, 0.621f),
            new Vector2(1.85f, 0.6141f),
            new Vector2(1.9f, 0.6072f),
            new Vector2(1.95f, 0.6003f),
            new Vector2(2f, 0.5934f),
            new Vector2(2.05f, 0.5867f),
            new Vector2(2.1f, 0.5804f),
            new Vector2(2.15f, 0.5743f),
            new Vector2(2.2f, 0.5685f),
            new Vector2(2.25f, 0.563f),
            new Vector2(2.3f, 0.5577f),
            new Vector2(2.35f, 0.5527f),
            new Vector2(2.4f, 0.5481f),
            new Vector2(2.45f, 0.5438f),
            new Vector2(2.5f, 0.5397f),
            new Vector2(2.6f, 0.5325f),
            new Vector2(2.7f, 0.5264f),
            new Vector2(2.8f, 0.5211f),
            new Vector2(2.9f, 0.5168f),
            new Vector2(3f, 0.5133f),
            new Vector2(3.1f, 0.5105f),
            new Vector2(3.2f, 0.5084f),
            new Vector2(3.3f, 0.5067f),
            new Vector2(3.4f, 0.5054f),
            new Vector2(3.5f, 0.504f),
            new Vector2(3.6f, 0.503f),
            new Vector2(3.7f, 0.5022f),
            new Vector2(3.8f, 0.5016f),
            new Vector2(3.9f, 0.501f),
            new Vector2(4f, 0.5006f),
            new Vector2(4.2f, 0.4998f),
            new Vector2(4.4f, 0.4995f),
            new Vector2(4.6f, 0.4992f),
            new Vector2(4.8f, 0.499f),
            new Vector2(5f, 0.4988f)
        };
        private static Vector3 gravity = new Vector3(0, -9.81f, 0);  
        public static float FormTrajectory2(float distance, Vector3 startPosition, Vector3 startVelocity, float bulletMassGrams, float bulletDiameterMillimeters, float ballisticCoefficient, out GStruct267[] trajectoryInfo)
        {
            trajectoryInfo = new GStruct267[600];
            float mass = bulletMassGrams / 1000f;
            float diameter = bulletDiameterMillimeters / 1000f;
            float crossSectionalArea = MathF.PI * (diameter / 2f) * (diameter / 2f);
            float timeStep = 0.01f;
            trajectoryInfo[0] = new GStruct267(0f, startPosition, startVelocity);
            for (int i = 1; i < trajectoryInfo.Length; i++)
            {
                GStruct267 previousState = trajectoryInfo[i - 1];
                Vector3 velocity = previousState.velocity;
                Vector3 position = previousState.position;
                float dragForce = mass * CalculateG1DragCoefficient(velocity.Length()) / ballisticCoefficient / (diameter * diameter) * 0.0014223f;
                Vector3 acceleration = gravity + Vector3.Normalize(velocity) * (-dragForce * 1.2f * crossSectionalArea * velocity.Length() * velocity.Length()) / (2f * mass);
                Vector3 newPosition = position + velocity * timeStep + 0.5f * acceleration * (timeStep * timeStep);
                Vector3 newVelocity = velocity + acceleration * timeStep;
                trajectoryInfo[i] = new GStruct267(i * timeStep, newPosition, newVelocity);
                if (newPosition.X > distance)
                {
                    return trajectoryInfo[i].time;
                }
            }
            return trajectoryInfo[^1].time;
        }
        public static float OptimizedFormTrajectory(
            float distance,
            Vector3 startPosition,
            Vector3 startVelocity,
            float bulletMassGrams,
            float bulletDiameterMillimeters,
            float ballisticCoefficient,
            out GStruct267[] trajectoryInfo)
        {
            const float timeStep = 0.01f;
            trajectoryInfo = new GStruct267[100]; // Smaller array for closer approximation
            float mass = bulletMassGrams / 1000f;
            float diameter = bulletDiameterMillimeters / 1000f;
            float crossSectionalArea = MathF.PI * (diameter / 2f) * (diameter / 2f);
            trajectoryInfo[0] = new GStruct267(0f, startPosition, startVelocity);
            for (int i = 1; i < trajectoryInfo.Length; i++)
            {
                var previous = trajectoryInfo[i - 1];
                Vector3 velocity = previous.velocity;
                float velocityLength = velocity.Length();

                float dragForce = CalculateG1DragCoefficient(velocityLength) * crossSectionalArea * velocityLength / (ballisticCoefficient * mass);
                Vector3 acceleration = gravity + (-dragForce * velocity / velocityLength);

                Vector3 newPosition = previous.position + velocity * timeStep;
                Vector3 newVelocity = velocity + acceleration * timeStep;

                trajectoryInfo[i] = new GStruct267(i * timeStep, newPosition, newVelocity);

                if (Vector3.Distance(startPosition, newPosition) >= distance)
                {
                    return i * timeStep; // Exit early
                }
            }
            return trajectoryInfo[^1].time; // Fallback return
        }               
    }      
}