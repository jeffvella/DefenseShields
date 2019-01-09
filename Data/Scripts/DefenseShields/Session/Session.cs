﻿namespace DefenseShields
{
    using System;
    using global::DefenseShields.Support;
    using Sandbox.Definitions;
    using Sandbox.ModAPI;
    using VRage.Game;
    using VRage.Game.Components;
    using VRage.Game.ModAPI;
    using VRageMath;
    using MyVisualScriptLogicProvider = Sandbox.Game.MyVisualScriptLogicProvider;

    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation | MyUpdateOrder.AfterSimulation, int.MinValue)]
    public partial class Session : MySessionComponentBase
    {
        public override void BeforeStart()
        {
            try
            {
                MpActive = MyAPIGateway.Multiplayer.MultiplayerActive;
                IsServer = MyAPIGateway.Multiplayer.IsServer;
                DedicatedServer = MyAPIGateway.Utilities.IsDedicated;

                var env = MyDefinitionManager.Static.EnvironmentDefinition;
                if (env.LargeShipMaxSpeed > MaxEntitySpeed) MaxEntitySpeed = env.LargeShipMaxSpeed;
                else if (env.SmallShipMaxSpeed > MaxEntitySpeed) MaxEntitySpeed = env.SmallShipMaxSpeed;

                Log.Init("debugdevelop.log");
                Log.Line($"Logging Started: Server:{IsServer} - Dedicated:{DedicatedServer} - MpActive:{MpActive}");

                MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, CheckDamage);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketIdShieldHit, ShieldHitReceived);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketIdEnforce, EnforcementReceived);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketIdO2GeneratorSettings, O2GeneratorSettingsReceived);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketIdControllerState, ControllerStateReceived);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketIdControllerSettings, ControllerSettingsReceived);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketIdModulatorSettings, ModulatorSettingsReceived);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketIdModulatorState, ModulatorStateReceived);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketIdEnhancerState, EnhancerStateReceived);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketIdO2GeneratorState, O2GeneratorStateReceived);
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketIdEmitterState, EmitterStateReceived);

                if (!MpActive) Players.TryAdd(MyAPIGateway.Session.Player.IdentityId, MyAPIGateway.Session.Player);
                MyVisualScriptLogicProvider.PlayerDisconnected += PlayerDisconnected;
                MyVisualScriptLogicProvider.PlayerRespawnRequest += PlayerConnected;
                if (!DedicatedServer)
                {
                    MyAPIGateway.TerminalControls.CustomControlGetter += CustomControls;
                }

                if (IsServer)
                {
                    Log.Line("LoadConf - Session: This is a server");
                    UtilsStatic.PrepConfigFile();
                    UtilsStatic.ReadConfigFile();
                }

                if (MpActive)
                {
                    SyncDist = MyAPIGateway.Session.SessionSettings.SyncDistance;
                    var syncDistBuffered = SyncDist + 500;
                    SyncDistSqr = syncDistBuffered * syncDistBuffered;

                    if (Enforced.Debug >= 3) Log.Line($"SyncDistSqr:{SyncDistSqr} - DistNorm:{SyncDist}");
                }
                else
                {
                    SyncDist = MyAPIGateway.Session.SessionSettings.ViewDistance;
                    var syncDistBuffered = SyncDist + 500;
                    SyncDistSqr = syncDistBuffered * syncDistBuffered;
                    if (Enforced.Debug >= 3) Log.Line($"SyncDistSqr:{SyncDistSqr} - DistNorm:{SyncDist}");
                }
                MyAPIGateway.Parallel.StartBackground(WebMonitor);
            }
            catch (Exception ex) { Log.Line($"Exception in BeforeStart: {ex}"); }
        }

        public override void Draw()
        {
            if (DedicatedServer) return;
            try
            {
                if (!EmpDraw.IsEmpty) EmpDrawExplosion();

                var compCount = Controllers.Count;
                if (compCount == 0) return;
                if (SphereOnCamera.Length != compCount) Array.Resize(ref SphereOnCamera, compCount);

                if (_count == 0 && _lCount == 0) OnCountThrottle = false;
                var onCount = 0;
                for (int i = 0; i < Controllers.Count; i++)
                {
                    var s = Controllers[i];
                    if (s.BulletCoolDown > -1)
                    {
                        s.BulletCoolDown++;
                        if (s.BulletCoolDown == 9) s.BulletCoolDown = -1;
                    }

                    if (s.WebCoolDown > -1)
                    {
                        s.WebCoolDown++;
                        if (s.WebCoolDown == 6) s.WebCoolDown = -1;
                    }

                    if (!s.WarmedUp || s.DsState.State.Lowered || s.DsState.State.Sleeping || s.DsState.State.Suspended || !s.DsState.State.EmitterWorking) continue;
                    var sp = new BoundingSphereD(s.DetectionCenter, s.BoundingRange);
                    if (!MyAPIGateway.Session.Camera.IsInFrustum(ref sp))
                    {
                        SphereOnCamera[i] = false;
                        continue;
                    }
                    SphereOnCamera[i] = true;
                    if (!s.Icosphere.ImpactsFinished) onCount++;
                }

                if (onCount >= OnCount)
                {
                    OnCount = onCount;
                    OnCountThrottle = true;
                }
                else if (!OnCountThrottle && _count == 59 && _lCount == 9) OnCount = onCount;

                for (int i = 0; i < Controllers.Count; i++)
                {
                    var s = Controllers[i];
                    if (!s.WarmedUp || s.DsState.State.Lowered || s.DsState.State.Sleeping || s.DsState.State.Suspended || !s.DsState.State.EmitterWorking) continue;
                    if (s.DsState.State.Online && SphereOnCamera[i]) s.Draw(OnCount, SphereOnCamera[i]);
                    else
                    {
                        if (s.DsState.State.Online)
                        {
                            if (!s.Icosphere.ImpactsFinished) s.Icosphere.StepEffects();
                        }
                        else if (s.IsWorking && SphereOnCamera[i]) s.DrawShieldDownIcon();
                    }
                }
            }
            catch (Exception ex) { Log.Line($"Exception in SessionDraw: {ex}"); }
        }

        #region Simulation
        public override void UpdateBeforeSimulation()
        {
            try
            {
                Timings();
                LoadBalancer();
                LogicUpdates();
            }
            catch (Exception ex) { Log.Line($"Exception in SessionBeforeSim: {ex}"); }
        }

        public override void UpdateAfterSimulation()
        {
            _autoResetEvent.Set();
        }
        #endregion

        #region Misc
        private void EmpDrawExplosion()
        {
            _effect.Stop();
            var stackCount = 0;
            var warHeadSize = 0;
            var epiCenter = Vector3D.Zero;

            foreach (var empChild in EmpDraw)
            {
                if (empChild.Value.CustomData == string.Empty || !empChild.Value.CustomData.Contains("!EMP"))
                {
                    stackCount++;
                    warHeadSize = empChild.Value.WarSize;
                    epiCenter += empChild.Value.Position;
                }
            }
            EmpDraw.Clear();
            if (stackCount == 0) return;

            epiCenter /= stackCount;
            var cameraPos = MyAPIGateway.Session.Camera.Position;
            var realDistanceSqr = Vector3D.DistanceSquared(epiCenter, cameraPos);
            if (realDistanceSqr > 4000000)
            {
                var testDir = Vector3D.Normalize(cameraPos - epiCenter);
                var newEpiCenter = cameraPos + (testDir * -1990);
                epiCenter = newEpiCenter;
            }

            var invSqrDist = UtilsStatic.InverseSqrDist(epiCenter, cameraPos, 2000);
            //Log.Line($"invSqrDist:{invSqrDist} - Dist:{Vector3D.Distance(epiCenter, MyAPIGateway.Session.Camera.Position)} - epicCenter:{epiCenter} - {stackCount}");
            if (invSqrDist <= 0) return;

            var matrix = MatrixD.CreateTranslation(epiCenter);
            MyParticlesManager.TryCreateParticleEffect(6667, out _effect, ref matrix, ref epiCenter, 0, true); // 15, 16, 24, 25, 28, (31, 32) 211 215 53
            if (_effect == null) return;

            var empSize = warHeadSize * stackCount;
            var radius = empSize * invSqrDist;
            var scale = 1 / invSqrDist;
            //Log.Line($"[Scaler] scale:{scale} - {radius}");
            _effect.UserRadiusMultiplier = (float)radius;
            _effect.UserEmitterScale = (float)scale;
            _effect.UserColorMultiplier = new Vector4(255, 255, 255, 10);
            _effect.Play();
        }

        public string ModPath()
        {
            var modPath = ModContext.ModPath;
            return modPath;
        }

        public override void LoadData()
        {
            Instance = this;
        }

        protected override void UnloadData()
        {
            Monitor = false;
            Instance = null;
            Enforced = null;
            ProtSets.Clean();

            _autoResetEvent.Set();
            _autoResetEvent = null;
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketIdShieldHit, ShieldHitReceived);
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketIdEnforce, EnforcementReceived);
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketIdO2GeneratorSettings, O2GeneratorSettingsReceived);
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketIdControllerState, ControllerStateReceived);
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketIdControllerSettings, ControllerSettingsReceived);
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketIdModulatorSettings, ModulatorSettingsReceived);
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketIdModulatorState, ModulatorStateReceived);
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketIdEnhancerState, EnhancerStateReceived);
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketIdO2GeneratorState, O2GeneratorStateReceived);
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketIdEmitterState, EmitterStateReceived);

            MyVisualScriptLogicProvider.PlayerDisconnected -= PlayerDisconnected;
            MyVisualScriptLogicProvider.PlayerRespawnRequest -= PlayerConnected;

            if (!DedicatedServer) MyAPIGateway.TerminalControls.CustomControlGetter -= CustomControls;

            Log.Line("Logging stopped.");
            Log.Close();
        }

        private void Timings()
        {
            _newFrame = true;
            Tick = (uint)(Session.ElapsedPlayTime.TotalMilliseconds * TickTimeDiv);
            Tick20 = Tick % 20 == 0;
            Tick60 = Tick % 60 == 0;
            Tick60 = Tick % 60 == 0;
            Tick180 = Tick % 180 == 0;
            Tick600 = Tick % 600 == 0;
            Tick1800 = Tick % 1800 == 0;

            if (_count++ == 59)
            {
                _count = 0;
                _lCount++;
                if (_lCount == 10)
                {
                    _lCount = 0;
                    _eCount++;
                    if (_eCount == 10)
                    {
                        _eCount = 0;
                    }
                }
            }
            if (!GameLoaded && Tick > 100)
            {
                if (!WarHeadLoaded && WarTerminalReset != null)
                {
                    WarTerminalReset.ShowInTerminal = true;
                    WarTerminalReset = null;
                    WarHeadLoaded = true;
                } 

                if (!MiscLoaded)
                {
                    MiscLoaded = true;
                    UtilsStatic.GetDefinitons();
                    if (!IsServer) Players.TryAdd(MyAPIGateway.Session.Player.IdentityId, MyAPIGateway.Session.Player);
                }
                GameLoaded = true;
            }
        }
        #endregion

        #region Events
        private void PlayerConnected(long id)
        {
            try
            {
                if (Players.ContainsKey(id))
                {
                    if (Enforced.Debug >= 3) Log.Line($"Player id({id}) already exists");
                    return;
                }
                MyAPIGateway.Multiplayer.Players.GetPlayers(null, myPlayer => FindPlayer(myPlayer, id));
            }
            catch (Exception ex) { Log.Line($"Exception in PlayerConnected: {ex}"); }
        }

        private void PlayerDisconnected(long l)
        {
            try
            {
                IMyPlayer removedPlayer;
                Players.TryRemove(l, out removedPlayer);
                if (Enforced.Debug >= 3) Log.Line($"Removed player, new playerCount:{Players.Count}");
            }
            catch (Exception ex) { Log.Line($"Exception in PlayerDisconnected: {ex}"); }
        }

        private bool FindPlayer(IMyPlayer player, long id)
        {
            if (player.IdentityId == id)
            {
                Players[id] = player;
                if (Enforced.Debug >= 3) Log.Line($"Added player: {player.DisplayName}, new playerCount:{Players.Count}");
            }
            return false;
        }
        #endregion
    }
}
