﻿using System;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game.Components;
using DefenseShields.Support;
using Sandbox.Game.Entities;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace DefenseShields
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false, "DSControlLarge", "DSControlSmall", "DSControlTable")]
    public partial class DefenseShields : MyGameLogicComponent
    {
        #region Simulation
        public override void OnAddedToContainer()
        {
            if (!_containerInited)
            {
                PowerPreInit();
                NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
                NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
                Shield = (IMyUpgradeModule)Entity;
                _containerInited = true;
            }
            if (Entity.InScene) OnAddedToScene();
        }

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            StorageSetup();
        }

        public override void OnAddedToScene()
        {
            try
            {
                if (Session.Enforced.Debug == 3) Log.Line($"OnAddedToScene: GridId:{Shield.CubeGrid.EntityId} - ShieldId [{Shield.EntityId}]");
                MyGrid = (MyCubeGrid)Shield.CubeGrid;
                MyCube = Shield as MyCubeBlock;
                RegisterEvents();
                AssignSlots();
                _resetEntity = true;
            }
            catch (Exception ex) { Log.Line($"Exception in OnAddedToScene: {ex}"); }
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            try
            {
                if (Shield.CubeGrid.Physics == null) return;
                _isServer = Session.IsServer;
                _isDedicated = Session.DedicatedServer;
                _mpActive = Session.MpActive;

                PowerInit();
                MyAPIGateway.Session.OxygenProviderSystem.AddOxygenGenerator(_ellipsoidOxyProvider);

                if (_isServer) Enforcements.SaveEnforcement(Shield, Session.Enforced, true);
                else Session.FunctionalShields.Add(this);

                Session.Controllers.Add(this);
                if (Session.Enforced.Debug == 3) Log.Line($"UpdateOnceBeforeFrame: ShieldId [{Shield.EntityId}]");
            }
            catch (Exception ex) { Log.Line($"Exception in Controller UpdateOnceBeforeFrame: {ex}"); }
        }

        public override void UpdateBeforeSimulation()
        {
            try
            {
                if (!EntityAlive()) return;
                if (!ShieldOn())
                {
                    if (Session.Enforced.Debug == 3 && WasOnline) Log.Line($"Off: WasOn:{WasOnline} - Online:{DsState.State.Online}({_prevShieldActive}) - Lowered:{DsState.State.Lowered} - Buff:{DsState.State.Buffer} - Sus:{DsState.State.Suspended} - EW:{DsState.State.EmitterWorking} - Perc:{DsState.State.ShieldPercent} - Wake:{DsState.State.Waking} - ShieldId [{Shield.EntityId}]");
                    if (WasOnline) OfflineShield();
                    else if (DsState.State.Message) ShieldChangeState();
                    return;
                }
                if (DsState.State.Online)
                {
                    if (_comingOnline) ComingOnlineSetup();
                    /*
                    var test = new LineD(MyAPIGateway.Session.Player.Character.PositionComp.WorldAABB.Center, MyAPIGateway.Session.Player.Character.PositionComp.WorldAABB.Center + MyAPIGateway.Session.Player.Character.WorldMatrix.Forward * 5000);
                    DsDebugDraw.DrawLine(test, Vector4.One);
                    var ray = new RayD(test.From, MyAPIGateway.Session.Camera.WorldMatrix.Forward);
                    var test2 = CustomCollision.IntersectEllipsoid(DetectMatrixOutsideInv, DetectionMatrix, ray);
                    var report = test2 == null ? "Null" : test2.ToString();
                    MyAPIGateway.Utilities.ShowMessage("", $"{report}");
                    */

                    if (_isServer)
                    {
                        var createHeTiming = _count == 6 && (_lCount == 1 || _lCount == 6);
                        if (GridIsMobile && createHeTiming) CreateHalfExtents();
                        if (_syncEnts) SyncThreadedEnts();

                        if (_mpActive && _count == 29)
                        {
                            var newPercentColor = UtilsStatic.GetShieldColorFromFloat(DsState.State.ShieldPercent);
                            if (newPercentColor != _oldPercentColor)
                            {
                                ShieldChangeState();
                                _oldPercentColor = newPercentColor;
                            }
                            else if (_lCount == 7 && _eCount == 7) ShieldChangeState();
                        }
                    }
                    else if (_syncEnts) SyncThreadedEnts();
                    if (!_isDedicated && _tick60) HudCheck();
                    if (_userDebugEnabled)
                    {
                        if (_tick600)
                        {
                            var message = $"User({MyAPIGateway.Multiplayer.Players.TryGetSteamId(Shield.OwnerId)}) Debugging\n" +
                                          $"On:{DsState.State.Online} - Active:{Session.ActiveShields.Contains(this)} - Suspend:{DsState.State.Suspended}\n" +
                                          $"Web:{Asleep} - Tick/LWoke:{_tick}/{LastWokenTick}\n" +
                                          $"Mo:{DsState.State.Mode} - Su:{DsState.State.Suspended} - Wa:{DsState.State.Waking}\n" +
                                          $"Np:{DsState.State.NoPower} - Lo:{DsState.State.Lowered} - Sl:{DsState.State.Sleeping}\n" +
                                          $"PSys:{MyGridDistributor?.SourcesEnabled} - PNull:{MyGridDistributor == null}\n" +
                                          $"MaxPower:{GridMaxPower} - AvailPower:{GridAvailablePower}\n" +
                                          $"Access:{DsState.State.ControllerGridAccess} - EmitterWorking:{DsState.State.EmitterWorking}\n" +
                                          $"ProtectedEnts:{ProtectedEntCache.Count} - ProtectMyGrid:{Session.GlobalProtect.ContainsKey(MyGrid)}\n" +
                                          $"ShieldMode:{ShieldMode} - isStatic:{IsStatic}\n" +
                                          $"IsMobile:{GridIsMobile} - isMoving:{ShieldComp.GridIsMoving}\n" +
                                          $"Sink:{_power} HP:{DsState.State.Buffer}: {ShieldMaxBuffer}";

                            if (!_isDedicated) MyAPIGateway.Utilities.ShowMessage("", message);
                            else Log.Line(message);
                        }
                    }
                }
                if (Session.Enforced.Debug == 3) _dsutil1.StopWatchReport($"PerfCon: Online: {DsState.State.Online} - Asleep:{Asleep} - Tick: {_tick} loop: {_lCount}-{_count}", 4);
            }
            catch (Exception ex) {Log.Line($"Exception in UpdateBeforeSimulation: {ex}"); }
        }

        public override bool IsSerialized()
        {
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                if (Shield.Storage != null)
                {
                    DsState.SaveState();
                    DsSet.SaveSettings();
                }
            }
            return false;
        }

        public override void OnBeforeRemovedFromContainer()
        {
            if (Entity.InScene) OnRemovedFromScene();
        }

        public override void OnRemovedFromScene()
        {
            try
            {
                if (Session.Enforced.Debug == 3) Log.Line($"OnRemovedFromScene: {ShieldMode} - GridId:{Shield.CubeGrid.EntityId} - ShieldId [{Shield.EntityId}]");
                if (ShieldComp?.DefenseShields == this)
                {
                    DsState.State.Online = false;
                    DsState.State.Suspended = true;
                    Shield.RefreshCustomInfo();
                    ShieldComp.DefenseShields = null;
                }
                RegisterEvents(false);
                InitEntities(false);
                IsWorking = false;
                IsFunctional = false;
                _shellPassive?.Render?.RemoveRenderObjects();
                _shellActive?.Render?.RemoveRenderObjects();
                ShieldEnt?.Render?.RemoveRenderObjects();
            }
            catch (Exception ex) { Log.Line($"Exception in OnRemovedFromScene: {ex}"); }
        }

        public override void MarkForClose()
        {
            try
            {
                base.MarkForClose();
            }
            catch (Exception ex) { Log.Line($"Exception in MarkForClose: {ex}"); }
        }

        public override void Close()
        {
            try
            {
                base.Close();
                if (Session.Enforced.Debug == 3) Log.Line($"Close: {ShieldMode} - ShieldId [{Shield.EntityId}]");
                if (Session.Controllers.Contains(this)) Session.Controllers.Remove(this);
                if (Session.FunctionalShields.Contains(this)) Session.FunctionalShields.Remove(this);
                if (Session.ActiveShields.Contains(this)) Session.ActiveShields.Remove(this);
                WasActive = false;
                Icosphere = null;
                InitEntities(false);
                MyAPIGateway.Session.OxygenProviderSystem.RemoveOxygenGenerator(_ellipsoidOxyProvider);

                _power = 0.0001f;
                if (_allInited) _sink.Update();
                if (ShieldComp?.DefenseShields == this) ShieldComp.DefenseShields = null;
            }
            catch (Exception ex) { Log.Line($"Exception in Close: {ex}"); }
        }
        #endregion
    }
}