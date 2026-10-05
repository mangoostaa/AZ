using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(BFEsp.Core), "BF ESP", "5.0.0", "AZ")]
[assembly: MelonGame(null, null)]

namespace BFEsp
{
    public class Core : MelonMod
    {
        // ESP
        private bool _esp = true;
        private bool _showEnemies = true;
        private bool _showTeam = false;
        private bool _healthEsp = true;      // 2D health bar overlay
        private bool _nameEsp = true;        // 2D name overlay
        private bool _extEsp = true;   // ESP 2D en overlay externo (invisible al compartir pantalla)

        private Color _nameColor = new Color(1f, 0.85f, 0.3f);
        private Color _visColor = new Color(1f, 0.92f, 0.2f);
        private Color _occlColor = new Color(1f, 0.12f, 0.12f);
        private Texture2D _pix;              // 1x1 white texture for bars
        private GUIStyle _nameStyle;
        private GUIStyle _creditStyle;

        // weapon (best-effort)
        private bool _noRecoil, _noSpread, _unlimAmmo, _fastFire, _noFireDelay;
        private float _fireMult = 2f;      // RPM multiplier
        private float _baseRPM = 0f;       // captured natural RPM
        private float _lastSetRPM = -1f;   // what we last wrote (to detect weapon switches)

        // aim assist: 0=Off 1=Memory(camera, hold key, smoothing) 2=Silent(bullet dir) 3=Magic(bullet at enemy)
        private int _aimMode = 1;
        private static readonly string[] ModeName = { "Off", "Memory", "Silent", "Magic" };
        private KeyCode _aimKey = KeyCode.Mouse1;   // hold to aim (Memory only)
        private float _aimFov = 140f;               // pixel radius (static)
        private float _aimSmooth = 1f;              // 1 = snap
        private int _aimPart = 0;                   // 0 head, 1 chest, 2 pelvis
        private bool _aimWallcheck = true;          // only lock visible enemies
        private bool _bindListening;

        // movement
        private bool _fly, _speedhack, _onTop;
        private float _flySpeed = 16f;
        private float _speedAmount = 12f;   // extra units/sec added on top of normal walk
        private float _footOffset = 1f;
        private float _extNext;
        private CharacterController _cc; private Rigidbody _rb; private float _moveScanT;
        private readonly List<Collider> _disabledCols = new List<Collider>();
        private bool _movePrev;
        private bool _scanFire; // F4 → scan gun timer fields to crack fire restrictions

        // view / misc
        private bool _bhop;

        // triggerbot
        private bool _triggerbot = false;
        private KeyCode _triggerKey = KeyCode.LeftAlt; // hold to activate
        private bool _weaponEsp = true;
        private bool _tbBindListening;
        private float _tbDelay = 0.06f;               // seconds between shots
        private float _tbNextShot;
        private static readonly float[] PartY = { 1.6f, 1.1f, 0.5f };
        private static readonly string[] PartName = { "Head", "Chest", "Pelvis" };

        // FOV circle
        private bool _showFov = true;
        private Color _fovColor = new Color(1f, 1f, 1f, 0.7f);

        // menu — 4 independent draggable windows
        private bool _menuOpen;
        private static bool _sMenuOpen;      // mirror so fire hooks can block click-through
        private Rect _winAim = new Rect(30f, 30f, 330f, 470f);   // left column, top
        private Rect _winEsp = new Rect(30f, 516f, 330f, 300f);  // left column, below aimbot
        private Rect _winMove = new Rect(378f, 30f, 320f, 232f);  // right column, top
        private Rect _winGun = new Rect(378f, 278f, 320f, 300f); // right column, below movement
        private Rect _winMisc = new Rect(378f, 594f, 320f, 250f); // right column, below gun
        private KeyCode _menuKey = KeyCode.Alpha5;
        private bool _menuBindListening;
        private CursorLockMode _prevLock = CursorLockMode.Locked;   // cursor state before the menu opened
        private bool _prevCursorVis;
        private KeyCode _espKey = KeyCode.None, _aimToggleKey = KeyCode.None, _flyKey = KeyCode.None;   // empty by default
        private bool _espKeyListen, _aimToggleListen, _flyKeyListen;
        private int _dragId;                 // 0=none, else which window is being dragged
        private Vector2 _dragOff;

        private Material _enemyVis, _enemyOccl, _teamVis, _teamOccl;
        private Texture2D _px;
        private float _nextScan;
        private PlayerScript[] _players = new PlayerScript[0];
        private PlayerScript _local;
        private bool _dumped;

        private class RS { public Renderer r; public Il2CppReferenceArray<Material> orig; }
        private readonly Dictionary<int, RS> _touched = new Dictionary<int, RS>();
        private readonly Dictionary<int, Transform> _headBones = new Dictionary<int, Transform>();

        private Transform GetHeadBone(PlayerScript p)
        {
            int id;
            try { id = p.GetInstanceID(); } catch { return null; }
            if (_headBones.TryGetValue(id, out var cached)) { if (cached != null) return cached; _headBones.Remove(id); }
            try
            {
                var ts = p.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < ts.Length; i++) { var t = ts[i]; if (t != null && t.name == "Head") { _headBones[id] = t; return t; } }
                for (int i = 0; i < ts.Length; i++) { var t = ts[i]; if (t != null && t.name.ToLower().Contains("head")) { _headBones[id] = t; return t; } }
            }
            catch { }
            return null;
        }

        private static bool _sNoRecoil, _sNoSpread;   // static mirrors for the Harmony prefixes
        private static int _sAimMode;
        private static Vector3 _sAimFwd = Vector3.forward;
        private static Core I;   // static handle so Harmony prefixes can reach instance data

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        private static readonly EnumWindowsProc _affinityScanCallback = AffinityScanWindow;
        private static int _affinityWindowsFound;

        public override void OnLateInitializeMelon()
        {
            I = this;
            LoadCfg();
            TryPatch();
            Overlay.Start(LoggerInstance.Msg);
            LoggerInstance.Msg("BF ESP v16. Press 5 = menu.");
        }

        // shared target: nearest alive enemy (body-part point) to screen center, within FOV px, wallchecked
        public bool TargetEnemy(out Vector3 aimPt) { PlayerScript t; return TargetEnemy(out aimPt, out t, false); }
        public bool TargetEnemy(out Vector3 aimPt, bool ignoreWall) { PlayerScript t; return TargetEnemy(out aimPt, out t, ignoreWall); }
        public bool TargetEnemy(out Vector3 aimPt, out PlayerScript target, bool ignoreWall)
        {
            aimPt = default; target = null;
            var cam = PickCamera(); if (cam == null || _local == null) return false;
            Vector3 camPos = cam.transform.position, fwd = cam.transform.forward;
            if (camPos.y < -100f) return false;
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f, bestD = _aimFov;
            PlayerScript best = null;
            var players = _players;
            for (int i = 0; i < players.Length; i++)
            {
                var p = players[i];
                if (p == null || p.isMine || !IsAlive(p) || IsTeam(p)) continue;
                var tr = p.transform; if (tr == null || tr.position.y < -100f) continue;
                var hb = GetHeadBone(p);
                Vector3 pt = hb != null ? hb.position : tr.position;
                if (_aimPart == 1) pt.y -= 0.25f; else if (_aimPart == 2) pt.y -= 0.5f;
                var sp = cam.WorldToScreenPoint(pt);
                if (sp.z <= 0f) continue;
                float dx = sp.x - cx, dy = sp.y - cy, d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d >= bestD) continue;
                if (_aimWallcheck && !ignoreWall) { Vector3 dir = pt - camPos; if (Physics.Linecast(camPos + fwd * 0.3f, pt - dir.normalized * 1.0f)) continue; }
                bestD = d; best = p; aimPt = pt;
            }
            target = best;
            return best != null;
        }

        // is world point visible from the active camera (no wall in the way)?
        private bool IsVisible(Vector3 pt)
        {
            var cam = PickCamera(); if (cam == null) return true;
            Vector3 camPos = cam.transform.position, fwd = cam.transform.forward;
            Vector3 dir = pt - camPos;
            return !Physics.Linecast(camPos + fwd * 0.3f, pt - dir.normalized * 1.0f);
        }

        private MethodInfo Find(Type t, string name)
        {
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (m.Name == name) return m;
            return null;
        }
        private HarmonyMethod HM(string n) { return new HarmonyMethod(typeof(Core).GetMethod(n, BindingFlags.Static | BindingFlags.NonPublic)); }
        private void LogSig(Type t, string name)
        {
            try
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != name) continue;
                    var ps = m.GetParameters();
                    var s = new StringBuilder("SIG " + m.ReturnType.Name + " " + name + "(");
                    for (int i = 0; i < ps.Length; i++) s.Append(ps[i].ParameterType.Name + (i < ps.Length - 1 ? ", " : ""));
                    LoggerInstance.Msg(s.ToString() + ")");
                }
            }
            catch { }
        }

        private void TryPatch()
        {
            try
            {
                var recoil = Find(typeof(FirstPersonGunView), "ComputeShotRecoil");
                if (recoil != null) { HarmonyInstance.Patch(recoil, prefix: HM(nameof(RecoilPrefix))); LoggerInstance.Msg("patched ComputeShotRecoil"); }
                else LoggerInstance.Warning("ComputeShotRecoil not found");

                var fb = Find(typeof(PlayerScript), "FireBullet");
                if (fb != null) { HarmonyInstance.Patch(fb, prefix: HM(nameof(FirePrefix)), postfix: HM(nameof(FireScanPostfix))); LoggerInstance.Msg("patched FireBullet (" + fb.GetParameters().Length + "p)"); }
                else LoggerInstance.Warning("FireBullet not found");
                var sas = Find(typeof(PlayerScript), "SemiAutoShoot");
                if (sas != null) { HarmonyInstance.Patch(sas, prefix: HM(nameof(SemiPrefix))); LoggerInstance.Msg("patched SemiAutoShoot"); }

                LogSig(typeof(PlayerScript), "SetAmmoInMagazine");
                LogSig(typeof(PlayerScript), "addAmmoToClip");
                LogSig(typeof(PlayerScript), "addAmmo");
                LogSig(typeof(PlayerScript), "reloadRoundsInMagazine");
                LogSig(typeof(PlayerScript), "reloadWeapon");
                LogSig(typeof(PlayerScript), "localReload");
                LogSig(typeof(PlayerScript), "finishReloadingGun");
                LogSig(typeof(PlayerScript), "mpFinishReload");
                LogSig(typeof(PlayerScript), "SetTotalAmmoForWeapon");
                // ---- room-admin RPCs: exact call signatures for the host-auth test ----
                // ---- hit-registration methods for true magic bullet ----
                LogSig(typeof(PlayerScript), "FireBullet");
                LogSig(typeof(PlayerScript), "FireOneShot");
                LogSig(typeof(PlayerScript), "PlayerHitPlayer");
                LogSig(typeof(PlayerScript), "PlayerHitPlayerOnce");
                LogSig(typeof(PlayerScript), "HitAtLocalPoint");
                LogSig(typeof(PlayerScript), "damagePlayer");
                LogSig(typeof(PlayerScript), "RpcVerifyHit");
                LogSig(typeof(PlayerScript), "RaycastBloodSplatter");
                // ---- hit-arg logging: capture real args on a clear-LOS hit so we can replay through walls ----
                var php = Find(typeof(PlayerScript), "PlayerHitPlayer");
                if (php != null) { HarmonyInstance.Patch(php, prefix: HM(nameof(HitLog_PHP))); LoggerInstance.Msg("log-patched PlayerHitPlayer"); }
                var halp = Find(typeof(PlayerScript), "HitAtLocalPoint");
                if (halp != null) { HarmonyInstance.Patch(halp, prefix: HM(nameof(HitLog_HALP))); LoggerInstance.Msg("log-patched HitAtLocalPoint"); }
                var dp = Find(typeof(PlayerScript), "damagePlayer");
                if (dp != null) { HarmonyInstance.Patch(dp, prefix: HM(nameof(HitLog_DP))); LoggerInstance.Msg("log-patched damagePlayer"); }
                var phpo = Find(typeof(PlayerScript), "PlayerHitPlayerOnce");
                if (phpo != null) { HarmonyInstance.Patch(phpo, prefix: HM(nameof(HitLog_PHPO))); LoggerInstance.Msg("log-patched PlayerHitPlayerOnce"); }
            }
            catch (Exception e) { LoggerInstance.Warning("TryPatch: " + e.Message); }
        }

        // return false = skip the original ComputeShotRecoil = no recoil applied
        private static bool RecoilPrefix() { return !_sNoRecoil; }

        private static string NameOf(PlayerScript p) { try { return p == null ? "null" : ("'" + p.gameObject.name + "' mine=" + p.isMine); } catch { return "?"; } }
        private static float _sPhpT, _sHalpT, _sDpT;
        private static void HitLog_PHP(PlayerScript __instance, int __0, float __1, Vector3 __2, byte __3, Vector3 __4, Vector3 __5, bool __6, bool __7)
        {
            try
            {
                if (Time.time - _sPhpT < 0.25f) return; _sPhpT = Time.time;
                MelonLogger.Msg("HIT>PlayerHitPlayer inst=" + NameOf(__instance) + " id=" + __0 + " dmg=" + __1.ToString("F1") + " pt=" + __2.ToString("F2") + " bodyPart=" + __3 + " v4=" + __4.ToString("F2") + " v5=" + __5.ToString("F2") + " b6=" + __6 + " b7=" + __7);
            }
            catch { }
        }
        private static void HitLog_HALP(PlayerScript __instance, Vector3 __0, PlayerScript __1)
        {
            try
            {
                if (Time.time - _sHalpT < 0.25f) return; _sHalpT = Time.time;
                MelonLogger.Msg("HIT>HitAtLocalPoint inst=" + NameOf(__instance) + " pt=" + __0.ToString("F2") + " target=" + NameOf(__1));
            }
            catch { }
        }
        private static void HitLog_DP(PlayerScript __instance, float __0, Transform __1, Vector3 __2, bool __3)
        {
            try
            {
                if (Time.time - _sDpT < 0.25f) return; _sDpT = Time.time;
                MelonLogger.Msg("HIT>damagePlayer inst=" + NameOf(__instance) + " dmg=" + __0.ToString("F1") + " bone=" + (__1 != null ? __1.name : "null") + " pt=" + __2.ToString("F2") + " b3=" + __3);
            }
            catch { }
        }
        private static float _sPhpoT;
        private static void HitLog_PHPO(PlayerScript __instance, int __0, float __1, Vector3 __2, byte __3, Vector3 __4, Vector3 __5, bool __6, long __7, byte __8)
        {
            try
            {
                if (Time.time - _sPhpoT < 0.2f) return; _sPhpoT = Time.time;
                MelonLogger.Msg("HIT>PlayerHitPlayerOnce inst=" + NameOf(__instance) + " id=" + __0 + " dmg=" + __1.ToString("F1") + " pt=" + __2.ToString("F2") + " bp=" + __3 + " v4=" + __4.ToString("F2") + " v5=" + __5.ToString("F2") + " b6=" + __6 + " l7=" + __7 + " by8=" + __8);
            }
            catch { }
        }

        // FireOneShot(int, float, Vector3 __2, Vector3 __3, byte, byte, double, int, long)
        // diagnostic: log which Vector3 is the direction (mag ~1) vs origin, for local player only
        // __2 = muzzle/origin, __3 = shot direction. Silent(2)=aim bullet at enemy; Magic(3)=spawn bullet at enemy (through walls).
        private static bool FirePrefix(PlayerScript __instance, ref Vector3 __2, ref Vector3 __3)
        {
            try
            {
                if (__instance == null || !__instance.isMine) return true;
                if (_sMenuOpen) return false;   // menu open → swallow the shot so clicking the UI doesn't fire into the game
                if (_sAimMode == 2 && I != null && I.TargetEnemy(out var sp))   // silent: redirect the bullet direction (tracer from gun)
                {
                    __3 = (sp - __2).normalized;
                    return true;
                }
                if (_sAimMode == 3 && I != null && I.TargetEnemy(out var pt, true))   // magic bullet: bullet spawns in front of the enemy
                {
                    Vector3 dir = (pt - __2).normalized;
                    __2 = pt - dir * 0.35f;                       // origin right in front of enemy → no gun tracer + raycast can't be wall-blocked
                    __3 = dir;
                    return true;
                }
                if (_sNoSpread) { var ct = __instance.cameraTransform; if (ct != null) __3 = ct.forward; }
            }
            catch { }
            return true;
        }
        private static float _sSemiLog;
        private static bool SemiPrefix()
        {
            if (_sMenuOpen) return false;   // menu open → don't semi-auto fire
            return true;
        }

        // scan for near-future timer fields (= next-shot/bolt/jump gates). __5 = FireBullet's double timestamp.
        private static bool _sScanFire; private static float _sScanLog;
        private static void FireScanPostfix(PlayerScript __instance, double __5)
        {
            try
            {
                if (!_sScanFire || __instance == null || !__instance.isMine) return;
                if (Time.time - _sScanLog < 0.5f) return;
                _sScanLog = Time.time;
                double now = __5;                 // network shot time (game clock)
                float ft = Time.time;
                var sb = new StringBuilder("SCAN now=" + now.ToString("F2") + " | ");
                ScanObj(sb, __instance, typeof(PlayerScript), now, ft, "PS");
                var gv = __instance.fpsGunView;
                if (gv != null) ScanObj(sb, gv, typeof(FirstPersonGunView), now, ft, "GV");
                MelonLogger.Msg(sb.ToString());
            }
            catch (Exception e) { MelonLogger.Msg("SCAN EX " + e.Message); }
        }
        private static void ScanObj(StringBuilder sb, object obj, Type t, double now, float ft, string tag)
        {
            foreach (var pi in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length != 0) continue;
                if (pi.PropertyType == typeof(double))
                {
                    double v; try { v = (double)pi.GetValue(obj); } catch { continue; }
                    if (v > now - 1.0 && v < now + 5.0) sb.Append(tag + "." + pi.Name + "(d)=" + v.ToString("F2") + " ");
                }
                else if (pi.PropertyType == typeof(float))
                {
                    float v; try { v = (float)pi.GetValue(obj); } catch { continue; }
                    if (v > ft - 1f && v < ft + 5f) sb.Append(tag + "." + pi.Name + "(f)=" + v.ToString("F2") + " ");
                }
            }
        }

        // ---------------- config save/load (invariant culture so decimals stay '.') ----------------
        private string CfgFile()
        {
            try { return Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BFEsp.cfg"); } catch { return "UserData/BFEsp.cfg"; }
        }
        private static string C(Color c) => c.r.ToString(CultureInfo.InvariantCulture) + "," + c.g.ToString(CultureInfo.InvariantCulture) + "," + c.b.ToString(CultureInfo.InvariantCulture);
        private static Color PC(string v, Color d) { try { var s = v.Split(','); return new Color(float.Parse(s[0], CultureInfo.InvariantCulture), float.Parse(s[1], CultureInfo.InvariantCulture), float.Parse(s[2], CultureInfo.InvariantCulture), d.a); } catch { return d; } }
        private static float F(string v, float d) { try { return float.Parse(v, CultureInfo.InvariantCulture); } catch { return d; } }
        private void SaveCfg()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("esp=" + _esp); sb.AppendLine("showEnemies=" + _showEnemies); sb.AppendLine("showTeam=" + _showTeam);
                sb.AppendLine("healthEsp=" + _healthEsp); sb.AppendLine("nameEsp=" + _nameEsp);
                sb.AppendLine("visColor=" + C(_visColor)); sb.AppendLine("occlColor=" + C(_occlColor)); sb.AppendLine("nameColor=" + C(_nameColor));
                sb.AppendLine("aimMode=" + _aimMode); sb.AppendLine("aimKey=" + (int)_aimKey); sb.AppendLine("aimFov=" + _aimFov.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("aimSmooth=" + _aimSmooth.ToString(CultureInfo.InvariantCulture)); sb.AppendLine("aimPart=" + _aimPart); sb.AppendLine("aimWallcheck=" + _aimWallcheck);
                sb.AppendLine("showFov=" + _showFov); sb.AppendLine("fovColor=" + C(_fovColor));
                sb.AppendLine("extEsp=" + _extEsp);
                sb.AppendLine("triggerbot=" + _triggerbot); sb.AppendLine("triggerKey=" + (int)_triggerKey); sb.AppendLine("tbDelay=" + _tbDelay.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("fastFire=" + _fastFire); sb.AppendLine("fireMult=" + _fireMult.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("noRecoil=" + _noRecoil); sb.AppendLine("noSpread=" + _noSpread); sb.AppendLine("unlimAmmo=" + _unlimAmmo); sb.AppendLine("noFireDelay=" + _noFireDelay); sb.AppendLine("flySpeed=" + _flySpeed.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("speedAmount=" + _speedAmount.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("bhop=" + _bhop);
                sb.AppendLine("aimx=" + _winAim.x.ToString(CultureInfo.InvariantCulture)); sb.AppendLine("aimy=" + _winAim.y.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("espx=" + _winEsp.x.ToString(CultureInfo.InvariantCulture)); sb.AppendLine("espy=" + _winEsp.y.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("movx=" + _winMove.x.ToString(CultureInfo.InvariantCulture)); sb.AppendLine("movy=" + _winMove.y.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("gunx=" + _winGun.x.ToString(CultureInfo.InvariantCulture)); sb.AppendLine("guny=" + _winGun.y.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("miscx=" + _winMisc.x.ToString(CultureInfo.InvariantCulture)); sb.AppendLine("miscy=" + _winMisc.y.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("menuKey=" + _menuKey); sb.AppendLine("espKey=" + _espKey); sb.AppendLine("aimToggleKey=" + _aimToggleKey); sb.AppendLine("flyKey=" + _flyKey);
                File.WriteAllText(CfgFile(), sb.ToString());
                LoggerInstance.Msg("settings saved -> " + CfgFile());
            }
            catch (Exception e) { LoggerInstance.Warning("save: " + e.Message); }
        }
        private void LoadCfg()
        {
            try
            {
                var p = CfgFile(); if (!File.Exists(p)) return;
                foreach (var line in File.ReadAllLines(p))
                {
                    int eq = line.IndexOf('='); if (eq < 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    switch (k)
                    {
                        case "esp": _esp = v == "True"; break;
                        case "showEnemies": _showEnemies = v == "True"; break;
                        case "showTeam": _showTeam = v == "True"; break;
                        case "healthEsp": _healthEsp = v == "True"; break;
                        case "nameEsp": _nameEsp = v == "True"; break;
                        case "visColor": _visColor = PC(v, _visColor); break;
                        case "occlColor": _occlColor = PC(v, _occlColor); break;
                        case "nameColor": _nameColor = PC(v, _nameColor); break;
                        case "aimMode": _aimMode = int.Parse(v); break;
                        case "aimKey": _aimKey = (KeyCode)int.Parse(v); break;
                        case "aimFov": _aimFov = F(v, _aimFov); if (_aimFov < 20f) _aimFov = 140f; break;
                        case "aimSmooth": _aimSmooth = F(v, _aimSmooth); break;
                        case "aimPart": _aimPart = int.Parse(v); break;
                        case "aimWallcheck": _aimWallcheck = v == "True"; break;
                        case "showFov": _showFov = v == "True"; break;
                        case "fovColor": _fovColor = PC(v, _fovColor); break;
                        case "triggerbot": _triggerbot = v == "True"; break;
                        case "triggerKey": _triggerKey = (KeyCode)int.Parse(v); break;
                        case "tbDelay": _tbDelay = F(v, _tbDelay); break;
                        case "fastFire": _fastFire = v == "True"; break;
                        case "fireMult": _fireMult = F(v, _fireMult); break;
                        case "noRecoil": _noRecoil = v == "True"; break;
                        case "noSpread": _noSpread = v == "True"; break;
                        case "unlimAmmo": _unlimAmmo = v == "True"; break;
                        case "noFireDelay": _noFireDelay = v == "True"; break;
                        case "flySpeed": _flySpeed = F(v, _flySpeed); break;
                        case "speedAmount": _speedAmount = F(v, _speedAmount); break;
                        case "bhop": _bhop = v == "True"; break;
                        case "aimx": _winAim.x = F(v, _winAim.x); break;
                        case "aimy": _winAim.y = F(v, _winAim.y); break;
                        case "espx": _winEsp.x = F(v, _winEsp.x); break;
                        case "espy": _winEsp.y = F(v, _winEsp.y); break;
                        case "movx": _winMove.x = F(v, _winMove.x); break;
                        case "movy": _winMove.y = F(v, _winMove.y); break;
                        case "gunx": _winGun.x = F(v, _winGun.x); break;
                        case "guny": _winGun.y = F(v, _winGun.y); break;
                        case "miscx": _winMisc.x = F(v, _winMisc.x); break;
                        case "miscy": _winMisc.y = F(v, _winMisc.y); break;
                        case "menuKey": try { var loadedMenuKey = (KeyCode)Enum.Parse(typeof(KeyCode), v); _menuKey = loadedMenuKey == KeyCode.Alpha9 || loadedMenuKey == KeyCode.Quote ? KeyCode.Alpha5 : loadedMenuKey; } catch { } break;
                        case "espKey": try { _espKey = (KeyCode)Enum.Parse(typeof(KeyCode), v); } catch { } break;
                        case "aimToggleKey": try { _aimToggleKey = (KeyCode)Enum.Parse(typeof(KeyCode), v); } catch { } break;
                        case "flyKey": try { _flyKey = (KeyCode)Enum.Parse(typeof(KeyCode), v); } catch { } break;
                        case "extEsp": _extEsp = v == "True"; break;

                    }
                }
                LoggerInstance.Msg("settings loaded");
            }
            catch (Exception e) { LoggerInstance.Warning("load: " + e.Message); }
        }

        public override void OnUpdate()
        {
            try
            {
                if (_bindListening) ListenBind(ref _aimKey, ref _bindListening);
                else if (_tbBindListening) ListenBind(ref _triggerKey, ref _tbBindListening);
                else if (_menuBindListening) ListenBind(ref _menuKey, ref _menuBindListening, true);
                else if (_espKeyListen) ListenBind(ref _espKey, ref _espKeyListen, true);
                else if (_aimToggleListen) ListenBind(ref _aimToggleKey, ref _aimToggleListen, true);
                else if (_flyKeyListen) ListenBind(ref _flyKey, ref _flyKeyListen, true);
                else
                {
                    if (_menuKey != KeyCode.None && Input.GetKeyDown(_menuKey)) ToggleMenu();
                    if (_espKey != KeyCode.None && Input.GetKeyDown(_espKey)) { _esp = !_esp; if (!_esp) RestoreAll(); }
                    if (_aimToggleKey != KeyCode.None && Input.GetKeyDown(_aimToggleKey)) _aimMode = _aimMode == 0 ? 1 : 0;
                    if (_flyKey != KeyCode.None && Input.GetKeyDown(_flyKey)) _fly = !_fly;
                }

                if (Input.GetKeyDown(KeyCode.F7)) _dumped = false;   // dev
                if (Input.GetKeyDown(KeyCode.F6)) DumpRooms();       // dev: dump room list
                if (Input.GetKeyDown(KeyCode.F8)) DumpWindowAffinities(); // dev: inspect capture-excluded windows
                if (Input.GetKeyDown(KeyCode.F4)) { _scanFire = !_scanFire; LoggerInstance.Msg("fire-scan " + _scanFire); }
                if (_menuOpen) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                _sMenuOpen = _menuOpen;
                _sNoRecoil = _noRecoil; _sNoSpread = _noSpread; _sScanFire = _scanFire; _sAimMode = _aimMode; // feed the Harmony prefixes
                { var pc = PickCamera(); if (pc != null) _sAimFwd = pc.transform.forward; }

                if (Time.time >= _nextScan)
                {
                    _nextScan = Time.time + 0.33f;
                    _players = Object.FindObjectsOfType<PlayerScript>();
                    _local = null;
                    for (int i = 0; i < _players.Length; i++) { var p = _players[i]; if (p != null && p.isMine) { _local = p; break; } }
                    if (!_dumped && _players.Length > 0) { _dumped = true; Dump(); }
                    UpdateChams();
                }
                ApplyGunMods();
                Triggerbot();
                // bhop: auto-jump the instant you land while holding space
                if (_bhop && _local != null && Input.GetKey(KeyCode.Space))
                {
                    try { if (_local.isPlayerGrounded) _local.Jump(); } catch (Exception e) { LoggerInstance.Warning("bhop: " + e.Message); _bhop = false; }
                }
                // ESP externo: 20 Hz es más que suficiente, y NO en OnGUI (que corre varias veces por frame)
                if (_extEsp && Time.time >= _extNext)
                {
                    _extNext = Time.time + 0.05f;
                    try { UpdateExternalEsp(); } catch { }
                }
            }
            catch (Exception e) { LoggerInstance.Warning("OnUpdate: " + e.Message); }
        }

        public override void OnLateUpdate()
        {
            try { Aimbot(); } catch (Exception e) { LoggerInstance.Warning("aim: " + e.Message); _aimMode = 0; }
            try { Movement(); } catch (Exception e) { LoggerInstance.Warning("move: " + e.Message); _fly = _speedhack = _onTop = false; }
        }

        private void Movement()
        {
            if (_local == null) { return; }
            if (Time.time > _moveScanT)
            {
                _moveScanT = Time.time + 1f;
                try { _cc = _local.GetComponentInChildren<CharacterController>(); } catch { }
                try { _rb = _local.GetComponentInChildren<Rigidbody>(); } catch { }
            }
            Transform root = _cc != null ? _cc.transform : (_rb != null ? _rb.transform : _local.transform);
            bool phase = _fly || _onTop; // both need colliders off to move through geometry

            // edge: disable player colliders so we phase freely; restore on exit
            if (phase && !_movePrev)
            {
                _disabledCols.Clear();
                try { var cols = _local.GetComponentsInChildren<Collider>(); for (int i = 0; i < cols.Length; i++) { var c = cols[i]; if (c != null && c.enabled) { c.enabled = false; _disabledCols.Add(c); } } } catch { }
                if (_rb != null) { try { _rb.isKinematic = true; } catch { } }
                RaycastHit fh; _footOffset = Physics.Raycast(root.position + Vector3.up * 1f, Vector3.down, out fh, 30f) ? Mathf.Clamp(root.position.y - fh.point.y, 0f, 3f) : 1f;
            }
            else if (!phase && _movePrev)
            {
                for (int i = 0; i < _disabledCols.Count; i++) { try { if (_disabledCols[i] != null) _disabledCols[i].enabled = true; } catch { } }
                _disabledCols.Clear();
                if (_rb != null) { try { _rb.isKinematic = false; } catch { } }
            }
            _movePrev = phase;

            var cam = PickCamera();
            if (_onTop && !_fly && cam != null)
            {
                // walk on top of whatever's below (phase through walls, land on top surfaces), normal-ish speed
                Vector3 f = cam.transform.forward; f.y = 0f; if (f.sqrMagnitude > 0f) f.Normalize();
                Vector3 r = cam.transform.right; r.y = 0f; if (r.sqrMagnitude > 0f) r.Normalize();
                Vector3 m = Vector3.zero;
                if (Input.GetKey(KeyCode.W)) m += f;
                if (Input.GetKey(KeyCode.S)) m -= f;
                if (Input.GetKey(KeyCode.D)) m += r;
                if (Input.GetKey(KeyCode.A)) m -= r;
                Vector3 pos = root.position;
                if (m.sqrMagnitude > 0.0001f) pos += m.normalized * 8f * Time.deltaTime;
                RaycastHit hit;
                if (Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, out hit, 100f)) pos.y = hit.point.y + _footOffset;
                root.position = pos;
                return;
            }
            if (_fly)
            {
                if (_rb != null) { try { _rb.velocity = Vector3.zero; } catch { } }
                Vector3 f = cam != null ? cam.transform.forward : root.forward;
                Vector3 r = cam != null ? cam.transform.right : root.right;
                Vector3 m = Vector3.zero;
                if (Input.GetKey(KeyCode.W)) m += f;
                if (Input.GetKey(KeyCode.S)) m -= f;
                if (Input.GetKey(KeyCode.D)) m += r;
                if (Input.GetKey(KeyCode.A)) m -= r;
                if (Input.GetKey(KeyCode.Space)) m += Vector3.up;
                if (Input.GetKey(KeyCode.LeftControl)) m -= Vector3.up;
                if (m.sqrMagnitude > 0.0001f) root.position += m.normalized * _flySpeed * Time.deltaTime;
                return;
            }

            // SPEEDHACK: add extra movement in the input direction via the CharacterController (collision-safe)
            if (_speedhack && _cc != null && _cc.enabled && cam != null)
            {
                Vector3 f = cam.transform.forward; f.y = 0f; if (f.sqrMagnitude > 0f) f.Normalize();
                Vector3 r = cam.transform.right; r.y = 0f; if (r.sqrMagnitude > 0f) r.Normalize();
                Vector3 m = Vector3.zero;
                if (Input.GetKey(KeyCode.W)) m += f;
                if (Input.GetKey(KeyCode.S)) m -= f;
                if (Input.GetKey(KeyCode.D)) m += r;
                if (Input.GetKey(KeyCode.A)) m -= r;
                if (m.sqrMagnitude > 0.0001f) { try { _cc.Move(m.normalized * _speedAmount * Time.deltaTime); } catch { } }
            }
        }

        private void ListenBind(ref KeyCode key, ref bool flag) { ListenBind(ref key, ref flag, false); }
        private void ListenBind(ref KeyCode key, ref bool flag, bool noMouse)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { flag = false; return; }
            foreach (KeyCode kc in Enum.GetValues(typeof(KeyCode)))
            {
                if (kc == KeyCode.None || kc == KeyCode.Escape) continue;
                if (noMouse && kc >= KeyCode.Mouse0 && kc <= KeyCode.Mouse6) continue;   // MB1/MB2/etc blacklisted for menu key
                if (Input.GetKeyDown(kc)) { key = kc; flag = false; return; }
            }
        }

        // ---------------- Triggerbot ----------------
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, System.UIntPtr dwExtraInfo);
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        private void Click() { mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, System.UIntPtr.Zero); mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, System.UIntPtr.Zero); }

        private void Triggerbot()
        {
            if (!_triggerbot || _local == null || _menuOpen) return;
            if (!Input.GetKey(_triggerKey)) return;
            if (Time.time < _tbNextShot) return;
            var cam = PickCamera(); if (cam == null) return;
            var ct = cam.transform;
            Vector3 o = ct.position, d = ct.forward;
            if (o.y < -100f) return;
            RaycastHit hit;
            if (Physics.Raycast(o + d * 0.4f, d, out hit, 500f))
            {
                var t = hit.transform; if (t == null) return;
                PlayerScript ps = null;
                try { ps = t.GetComponentInParent<PlayerScript>(); } catch { }
                if (ps != null && !ps.isMine && IsAlive(ps) && !IsTeam(ps))
                {
                    Click();
                    _tbNextShot = Time.time + _tbDelay;
                }
            }
        }

        private Camera PickCamera()
        {
            var main = Camera.main;
            if (main != null) return main;
            Camera best = null; float bestDepth = float.NegativeInfinity;
            var cams = Camera.allCameras;
            for (int i = 0; i < cams.Length; i++) { var c = cams[i]; if (c == null) continue; if (c.depth >= bestDepth) { bestDepth = c.depth; best = c; } }
            return best;
        }
        private readonly Dictionary<string, PropertyInfo> _psProp = new Dictionary<string, PropertyInfo>();
        private void SetF(string prop, float v)
        {
            try
            {
                if (!_psProp.TryGetValue(prop, out var pi))
                {
                    pi = typeof(PlayerScript).GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _psProp[prop] = pi;
                }
                if (pi != null && pi.CanWrite && pi.PropertyType == typeof(float)) pi.SetValue(_local, v);
            }
            catch { }
        }
        private bool IsTeam(PlayerScript p) { try { return _local != null && p.SameTeamAs(_local); } catch { return false; } }
        private bool IsAlive(PlayerScript p) { try { return p._health.Value > 0f; } catch { return true; } }

        // ---------------- Aimbot ----------------
        private void Aimbot() // Memory mode: move the view/aim toward the target (hold key, smoothing)
        {
            if (_aimMode != 1 || _local == null) return;
            if (!Input.GetKey(_aimKey)) return;
            var cam = PickCamera();
            if (cam == null) return;
            var camTf = cam.transform;
            if (!TargetEnemy(out Vector3 pt)) return;
            var target = Quaternion.LookRotation(pt - camTf.position);
            // frame-rate independent smoothing: 1 = instant lock; lower = smooth ease that still tracks
            float t = _aimSmooth >= 0.999f ? 1f : 1f - Mathf.Pow(1f - _aimSmooth, Time.deltaTime * 60f);
            var rot = Quaternion.Slerp(camTf.rotation, target, t);
            try { _local.rotation = rot; } catch { }
            try { var ct = _local.cameraTransform; if (ct != null) ct.rotation = rot; } catch { }
            camTf.rotation = rot;
        }

        // ---------------- Gun mods (best-effort) ----------------
        private void ApplyGunMods()
        {
            if (_local == null) return;
            if (_noRecoil || _noSpread || _fastFire)
            {
                try
                {
                    var gs = _local.mpCurrentGunStats;
                    if (gs != null)
                    {
                        if (_noRecoil) { gs.gunUpKick = 0f; gs.gunBackKick = 0f; gs.maxGunBackKick = 0f; }
                        if (_noSpread) { gs.hipMinSpread = 0f; gs.hipMaxSpread = 0f; gs.adsMinSpread = 0f; gs.adsMaxSpread = 0f; gs.spreadIncrease = 0f; }
                        if (_fastFire)
                        {
                            float rpm = gs.RPM;
                            // if the game changed RPM (weapon switch), capture it as the new natural base
                            if (rpm > 10f && Mathf.Abs(rpm - _lastSetRPM) > 1f) _baseRPM = rpm;
                            if (_baseRPM > 10f) { float t = _baseRPM * _fireMult; gs.RPM = t; _lastSetRPM = t; }
                        }
                    }
                }
                catch (Exception e) { LoggerInstance.Warning("gunstats: " + e.Message); _noRecoil = _noSpread = _fastFire = false; }
            }
            if (_unlimAmmo)
            {
                try { _local.SetAmmoInMagazine(_local.mpCurrentWeaponType.Value, 90); }
                catch (Exception e) { LoggerInstance.Warning("ammo: " + e.Message); _unlimAmmo = false; }
            }
            if (_noFireDelay)
            {
                float past = Time.time - 10f;
                SetF("timeLastFiredUnsuppressedBullet", past);
                SetF("JJHGGLBDEPB", past);
                SetF("FKJKDONJHJD", past);
                SetF("JKNILOFLDJE", past);
                SetF("FLHHCACIOOL", past);
            }
        }

        // ---------------- Chams ----------------
        private Material MakeCham(Color col, int ztest)
        {
            Shader sh = Shader.Find("Hidden/Internal-Colored"); if (sh == null) sh = Shader.Find("Sprites/Default"); if (sh == null) sh = Shader.Find("Unlit/Color");
            var m = new Material(sh); m.hideFlags = HideFlags.HideAndDontSave;
            try { m.SetInt("_ZTest", ztest); } catch { }
            try { m.SetInt("_ZWrite", 0); } catch { }
            try { m.SetInt("_Cull", 0); } catch { }
            SetMatColor(m, col); return m;
        }
        private void SetMatColor(Material m, Color c) { try { m.SetColor("_Color", c); } catch { } try { m.color = c; } catch { } }

        private void EnsureMats()
        {
            if (_enemyOccl == null)
            {
                _enemyVis = MakeCham(_visColor, 4); _enemyOccl = MakeCham(_occlColor, 5);
                _teamVis = MakeCham(new Color(0.2f, 0.9f, 1f), 4); _teamOccl = MakeCham(new Color(0.2f, 1f, 0.35f), 5);
            }
            SetMatColor(_enemyVis, _visColor); SetMatColor(_enemyOccl, _occlColor);
        }

        private void UpdateChams()
        {
            if (!_esp || _extEsp) { RestoreAll(); return; }
            EnsureMats();
            // occlusion culling hides far/occluded meshes even with our through-wall material → turn it off
            try { var oc = PickCamera(); if (oc != null) oc.useOcclusionCulling = false; } catch { }
            var want = new HashSet<int>();
            var players = _players;
            for (int i = 0; i < players.Length; i++)
            {
                var p = players[i];
                if (p == null || p.isMine) continue;
                var tr = p.transform;
                if (tr == null || tr.position.y < -100f || !IsAlive(p)) continue;
                bool team = IsTeam(p);
                if (team && !_showTeam) continue;
                if (!team && !_showEnemies) continue;
                var visMat = team ? _teamVis : _enemyVis; var occlMat = team ? _teamOccl : _enemyOccl;
                // force LOD0 so the game's distance-based LOD culling can't hide the mesh (visible=False fix)
                try { var lods = p.GetComponentsInChildren<LODGroup>(true); for (int k = 0; k < lods.Length; k++) if (lods[k] != null) lods[k].ForceLOD(0); } catch { }
                Il2CppArrayBase<Renderer> rends;
                try { rends = p.GetComponentsInChildren<Renderer>(true); } catch { continue; }
                if (rends == null) continue;
                for (int j = 0; j < rends.Length; j++)
                {
                    var r = rends[j]; if (r == null) continue;
                    int id; try { id = r.GetInstanceID(); } catch { continue; }
                    want.Add(id);
                    if (!_touched.ContainsKey(id)) { try { _touched[id] = new RS { r = r, orig = r.materials }; } catch { continue; } }
                    try { var smr = r.TryCast<SkinnedMeshRenderer>(); if (smr != null) smr.updateWhenOffscreen = true; r.enabled = true; r.forceRenderingOff = false; r.materials = new Il2CppReferenceArray<Material>(new Material[] { visMat, occlMat }); } catch { }
                }
            }
            if (_touched.Count > 0)
            {
                var remove = new List<int>();
                foreach (var kv in _touched) if (!want.Contains(kv.Key)) remove.Add(kv.Key);
                for (int i = 0; i < remove.Count; i++) { var rs = _touched[remove[i]]; try { if (rs.r != null) rs.r.materials = rs.orig; } catch { } _touched.Remove(remove[i]); }
            }
        }

        private void RestoreAll()
        {
            if (_touched.Count == 0) return;
            foreach (var kv in _touched) { try { if (kv.Value.r != null) kv.Value.r.materials = kv.Value.orig; } catch { } }
            _touched.Clear();
        }
        public override void OnDeinitializeMelon() { try { RestoreAll(); } catch { } }

        // ---------------- UI ----------------
        private Material _glMat;
        // crisp GL circle: 1px lines at any radius, no texture/pixelation/thickness-scaling
        private void DrawCircle(float cx, float cy, float r, Color col)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (_glMat == null)
            {
                var sh = Shader.Find("Hidden/Internal-Colored");
                _glMat = new Material(sh); _glMat.hideFlags = HideFlags.HideAndDontSave;
                _glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _glMat.SetInt("_Cull", 0); _glMat.SetInt("_ZWrite", 0); _glMat.SetInt("_ZTest", 8);
            }
            _glMat.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.LINES);
            GL.Color(col);
            int seg = 96;
            float py = Screen.height - cy;                 // GL origin is bottom-left
            float px0 = cx + r, py0 = py;
            for (int i = 1; i <= seg; i++)
            {
                float a = (i / (float)seg) * Mathf.PI * 2f;
                float px1 = cx + Mathf.Cos(a) * r, py1 = py + Mathf.Sin(a) * r;
                GL.Vertex3(px0, py0, 0f);
                GL.Vertex3(px1, py1, 0f);
                px0 = px1; py0 = py1;
            }
            GL.End();
            GL.PopMatrix();
        }
        private static readonly Color[] Palette = {
            new Color(1f,0f,0f), new Color(1f,0.5f,0f), new Color(1f,1f,0f), new Color(0.5f,1f,0f),
            new Color(0f,1f,0f), new Color(0f,1f,0.6f), new Color(0f,1f,1f), new Color(0f,0.5f,1f),
            new Color(0.2f,0.2f,1f), new Color(0.6f,0f,1f), new Color(1f,0f,1f), new Color(1f,0f,0.5f),
            new Color(1f,1f,1f), new Color(0.5f,0.5f,0.5f), new Color(0f,0f,0f)
        };
        private Il2CppReferenceArray<GUILayoutOption> _swOpt;
        private void Swatches(string label, ref Color target)
        {
            if (_swOpt == null) _swOpt = new Il2CppReferenceArray<GUILayoutOption>(new GUILayoutOption[] { GUILayout.Width(17f), GUILayout.Height(15f) });
            GUILayout.Label(label);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Palette.Length; i++)
            {
                GUI.backgroundColor = Palette[i];
                if (GUILayout.Button("", _swOpt)) target = new Color(Palette[i].r, Palette[i].g, Palette[i].b, target.a);
            }
            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();
        }

        private float GetHealth(PlayerScript p, out float max)
        {
            max = 100f;
            try { float mx = p.NHLALKFMOHG; if (mx > 1f) max = mx; } catch { }   // mpMaxHealth
            try { float h = p.CEBCIILAFAB; if (h >= 0f) return Mathf.Clamp(h, 0f, max); } catch { }   // mpHealth
            try { return Mathf.Clamp(p._health.Value, 0f, max); } catch { }
            return max;
        }
        private string GetName(PlayerScript p)
        {
            try { string n = p.username; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            try { string n = p.syncUsername.Value; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return "?";
        }

        private string GetWeapon(PlayerScript p)
        {
            try
            {
                // mpCurrentWeaponType es un SyncVar → hay que leer .Value
                var wt = p.mpCurrentWeaponType;
                if (wt != null)
                {
                    object val = null;
                    try { val = wt.Value; } catch { }

                    if (val != null)
                    {
                        string s = val.ToString();
                        // Filtrar basura de tipos de red
                        if (!string.IsNullOrEmpty(s) &&
                            !s.Contains("Sync") &&
                            !s.Contains("FishNet") &&
                            s != "0")
                            return s;
                    }
                }
            }
            catch { }

            try
            {
                var gun = p.mpGunScript;
                if (gun != null)
                {
                    string n = gun.name;
                    if (!string.IsNullOrEmpty(n))
                        return n.Replace("(Clone)", "").Trim();
                }
            }
            catch { }

            return "?";
        }

        // text with a 1px black shadow so it reads over any background
        private void ShadowText(Rect r, string s, Color col)
        {
            if (_nameStyle == null) { _nameStyle = new GUIStyle(GUI.skin.label); _nameStyle.alignment = TextAnchor.MiddleCenter; _nameStyle.fontSize = 12; _nameStyle.fontStyle = FontStyle.Bold; }
            var sh = r; sh.x += 1f; sh.y += 1f;
            GUI.color = new Color(0f, 0f, 0f, 0.9f); GUI.Label(sh, s, _nameStyle);
            GUI.color = col; GUI.Label(r, s, _nameStyle);
            GUI.color = Color.white;
        }

        // world-space AABB of the player's body (updates with crouch/animation, unlike transform.position)
        private bool GetBodyBounds(PlayerScript p, out Bounds bounds)
        {
            bounds = default; bool has = false;
            try
            {
                var smrs = p.GetComponentsInChildren<SkinnedMeshRenderer>(false);
                for (int i = 0; i < smrs.Length; i++)
                {
                    var s = smrs[i]; if (s == null || !s.enabled) continue;
                    if (!has) { bounds = s.bounds; has = true; } else bounds.Encapsulate(s.bounds);
                }
            }
            catch { }
            return has;
        }

        private void DrawInfoEsp()
        {
            if (!_esp || (!_healthEsp && !_nameEsp)) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var cam = PickCamera(); if (cam == null) return;
            if (_pix == null) { _pix = new Texture2D(1, 1); _pix.SetPixel(0, 0, Color.white); _pix.Apply(); }
            var players = _players;
            for (int i = 0; i < players.Length; i++)
            {
                var p = players[i];
                if (p == null || p.isMine) continue;
                var tr = p.transform; if (tr == null || tr.position.y < -100f || !IsAlive(p)) continue;
                bool team = IsTeam(p);
                if (team && !_showTeam) continue;
                if (!team && !_showEnemies) continue;

                Vector3 topW, botW;
                Bounds bb;
                if (GetBodyBounds(p, out bb))
                {
                    topW = new Vector3(bb.center.x, bb.max.y, bb.center.z);   // real head top
                    botW = new Vector3(bb.center.x, bb.min.y, bb.center.z);   // real feet
                }
                else
                {
                    var head = GetHeadBone(p);
                    topW = head != null ? head.position + Vector3.up * 0.25f : tr.position + Vector3.up * 0.9f;
                    botW = tr.position - Vector3.up * 0.9f;
                }
                var spTop = cam.WorldToScreenPoint(topW);
                var spBot = cam.WorldToScreenPoint(botW);
                if (spTop.z <= 0f) continue;
                float topY = Screen.height - spTop.y;
                float botY = Screen.height - spBot.y;
                float h = Mathf.Abs(botY - topY); if (h < 8f) h = 8f;
                float cx = (spTop.x + spBot.x) * 0.5f;

                if (_healthEsp)
                {
                    float max; float hp = GetHealth(p, out max);
                    float ratio = max > 0f ? Mathf.Clamp01(hp / max) : 1f;
                    float barW = Mathf.Clamp(h * 0.5f, 22f, 170f);   // width scales with player size
                    float bh = 5f;
                    float bx = cx - barW * 0.5f;                      // centered under the feet
                    float by = botY + 5f;                            // just below the feet
                    GUI.color = new Color(0f, 0f, 0f, 0.7f); GUI.DrawTexture(new Rect(bx - 1f, by - 1f, barW + 2f, bh + 2f), _pix);
                    float fw = barW * ratio;
                    GUI.color = Color.Lerp(new Color(1f, 0.15f, 0.15f), new Color(0.25f, 1f, 0.25f), ratio);
                    GUI.DrawTexture(new Rect(bx, by, fw, bh), _pix);   // fill left → right
                    GUI.color = Color.white;
                }
                if (_nameEsp)
                {
                    string nm = GetName(p);
                    string wep = GetWeapon(p);
                    string text = (wep != "?" && wep.Length > 0) ? (nm + "  [" + wep + "]") : nm;
                    Color nc = team ? new Color(0.4f, 1f, 0.5f) : _nameColor;
                    ShadowText(new Rect(cx - 110f, topY - 18f, 220f, 16f), text, nc);
                }
            }
        }

        public override void OnGUI()
        {
            try
            {
                GUI.color = Color.white;
                if (_extEsp)
                {
                    // ESP externo: ya se actualiza desde OnUpdate a 20 Hz; aquí no tocamos nada
                }
                else
                {
                    GUI.Label(new Rect(12f, 12f, 800f, 22f), "BF ESP  chams:" + _touched.Count + "  aim:" + ModeName[_aimMode] + "   [press 5]");
                    DrawInfoEsp();
                    if (_showFov && _aimMode != 0)
                        DrawCircle(Screen.width * 0.5f, Screen.height * 0.5f, _aimFov, _fovColor);
                }

                if (!_menuOpen) return;

                DrawWindow(ref _winAim, 1, "AIMBOT", AimContent);
                DrawWindow(ref _winEsp, 2, "ESP", EspContent);
                DrawWindow(ref _winMove, 3, "MOVEMENT", MoveContent);
                DrawWindow(ref _winGun, 4, "GUN MODS", GunContent);
                DrawWindow(ref _winMisc, 5, "MISC", MiscContent);
            }
            catch (Exception e) { LoggerInstance.Warning("OnGUI: " + e.Message); }
        }

        // ---- ESP externo: computa la escena (throttled desde OnUpdate, NO desde OnGUI) ----
        private void UpdateExternalEsp()
        {
            if (!_esp || _local == null) { Overlay.SetScene(null, "BF ESP (off)", false, 0); return; }
            var cam = PickCamera();
            if (cam == null || cam.transform.position.y < -100f)
            {
                Overlay.SetScene(null, "BF ESP  aim:" + ModeName[_aimMode] + " (lobby, esperando partida)", false, 0);
                return;
            }

            var list = new List<Overlay.EspEntry>();
            var players = _players;
            for (int i = 0; i < players.Length; i++)
            {
                var p = players[i];
                if (p == null || p.isMine) continue;
                var tr = p.transform; if (tr == null || tr.position.y < -100f || !IsAlive(p)) continue;
                bool team = IsTeam(p);
                if (team && !_showTeam) continue;
                if (!team && !_showEnemies) continue;

                Vector3 topW, botW; Bounds bb;
                if (GetBodyBounds(p, out bb)) { topW = new Vector3(bb.center.x, bb.max.y, bb.center.z); botW = new Vector3(bb.center.x, bb.min.y, bb.center.z); }
                else { var hb = GetHeadBone(p); topW = hb != null ? hb.position + Vector3.up * 0.25f : tr.position + Vector3.up * 0.9f; botW = tr.position - Vector3.up * 0.9f; }

                var spT = cam.WorldToScreenPoint(topW); var spB = cam.WorldToScreenPoint(botW);
                if (spT.z <= 0f || spB.z <= 0f) continue;
                float topY = Screen.height - spT.y, botY = Screen.height - spB.y;
                float h = Mathf.Abs(botY - topY); if (h < 12f) h = 12f;
                float bw = Mathf.Clamp(h * 0.42f, 8f, 240f);
                float cx = (spT.x + spB.x) * 0.5f;

                float max; float hp = GetHealth(p, out max);
                float ratio = max > 0f ? Mathf.Clamp01(hp / max) : 1f;

                bool hidden = !IsVisible(Vector3.Lerp(botW, topW, 0.75f));   // ¿pared en medio?
                Color c = hidden ? _occlColor : (team ? new Color(0.2f, 0.9f, 1f) : _visColor);
                if (c.r > 0.99f && c.g < 0.01f && c.b > 0.99f) c.g = 0.02f;      // magenta = color key, evitar

                var e = new Overlay.EspEntry
                {
                    X = cx - bw * 0.5f,
                    Y = topY,
                    W = bw,
                    H = h,
                    Hp = ratio,
                    Name = _nameEsp ? GetName(p) : "",
                    Weapon = (_weaponEsp && _nameEsp) ? GetWeapon(p) : "",
                    Dist = Vector3.Distance(cam.transform.position, tr.position),
                    BoxColor = (uint)((int)(c.r * 255f) | ((int)(c.g * 255f) << 8) | ((int)(c.b * 255f) << 16)),
                    Team = team
                };
                list.Add(e);
            }
            Overlay.SetScene(list, "BF ESP  aim:" + ModeName[_aimMode], _showFov && _aimMode != 0, (int)_aimFov);
        }


        private void DrawWindow(ref Rect win, int id, string title, Action content)
        {
            var ev = Event.current;
            if (ev != null)
            {
                Rect bar = new Rect(win.x, win.y, win.width, 24f);
                if (ev.type == EventType.MouseDown && bar.Contains(ev.mousePosition) && _dragId == 0) { _dragId = id; _dragOff = ev.mousePosition - new Vector2(win.x, win.y); }
                else if (ev.type == EventType.MouseUp && _dragId == id) _dragId = 0;
                if (_dragId == id && ev.type == EventType.MouseDrag) { win.x = ev.mousePosition.x - _dragOff.x; win.y = ev.mousePosition.y - _dragOff.y; }
            }
            if (_pix == null) { _pix = new Texture2D(1, 1); _pix.SetPixel(0, 0, Color.white); _pix.Apply(); }
            // opaque fill + coloured title strip
            GUI.color = new Color(0.09f, 0.10f, 0.13f, 1f); GUI.DrawTexture(win, _pix);
            GUI.color = new Color(0.16f, 0.34f, 0.55f, 1f); GUI.DrawTexture(new Rect(win.x, win.y, win.width, 24f), _pix);
            GUI.color = Color.white;
            // credits, top-right of the title bar
            if (_creditStyle == null) { _creditStyle = new GUIStyle(GUI.skin.label); _creditStyle.alignment = TextAnchor.MiddleRight; _creditStyle.fontStyle = FontStyle.Bold; }
            GUI.Label(new Rect(win.x, win.y + 2f, win.width - 8f, 20f), "dc: 8832", _creditStyle);
            GUILayout.BeginArea(new Rect(win.x + 8f, win.y + 4f, win.width - 16f, win.height - 8f));
            GUILayout.Label(title + "   :: drag");
            content();
            GUILayout.EndArea();
        }

        private void AimContent()
        {
            GUILayout.Label("-- aim assist (pick one) --");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 4; i++) { GUI.color = _aimMode == i ? Color.green : Color.white; if (GUILayout.Button(ModeName[i])) _aimMode = i; }
            GUI.color = Color.white;
            GUILayout.EndHorizontal();
            _aimWallcheck = GUILayout.Toggle(_aimWallcheck, " Wallcheck (visible only)");
            if (GUILayout.Button(_bindListening ? "press any key..." : "Memory key: " + _aimKey)) _bindListening = true;
            if (GUILayout.Button("Target: " + PartName[_aimPart])) _aimPart = (_aimPart + 1) % 3;
            GUILayout.Label("FOV radius " + _aimFov.ToString("F0") + "px");
            _aimFov = GUILayout.HorizontalSlider(_aimFov, 20f, 500f);
            GUILayout.Label("Smoothing (Memory only) " + _aimSmooth.ToString("F2"));
            _aimSmooth = GUILayout.HorizontalSlider(_aimSmooth, 0.03f, 1f);
            _showFov = GUILayout.Toggle(_showFov, " Show FOV circle");
            Swatches("FOV circle color:", ref _fovColor);
            GUILayout.Space(4f);
            GUILayout.Label("-- triggerbot --");
            _triggerbot = GUILayout.Toggle(_triggerbot, " Triggerbot enabled");
            if (GUILayout.Button(_tbBindListening ? "press any key..." : "Trigger key: " + _triggerKey)) _tbBindListening = true;
            GUILayout.Label("Fire delay " + (_tbDelay * 1000f).ToString("F0") + "ms");
            _tbDelay = GUILayout.HorizontalSlider(_tbDelay, 0.01f, 0.3f);
        }

        private void EspContent()
        {
            _esp = GUILayout.Toggle(_esp, " ESP master");
            _showEnemies = GUILayout.Toggle(_showEnemies, " Chams enemies");
            _showTeam = GUILayout.Toggle(_showTeam, " Chams teammates");
            _healthEsp = GUILayout.Toggle(_healthEsp, " Health bar ESP");
            _nameEsp = GUILayout.Toggle(_nameEsp, " Name ESP");
            _weaponEsp = GUILayout.Toggle(_weaponEsp, " Weapon ESP");
            _extEsp = GUILayout.Toggle(_extEsp, " ESP externo 2D (invisible en captura)");
            if (_extEsp) GUILayout.Label("   (chams internos OFF mientras esto esté activo)");
            Swatches("name color:", ref _nameColor);
            Swatches("visible color:", ref _visColor);
            Swatches("through-wall color:", ref _occlColor);
        }

        private void MoveContent()
        {
            _fly = GUILayout.Toggle(_fly, " Fly (WASD + Space/Ctrl)");
            GUILayout.Label("Fly speed " + _flySpeed.ToString("F0"));
            _flySpeed = GUILayout.HorizontalSlider(_flySpeed, 4f, 60f);
            _speedhack = GUILayout.Toggle(_speedhack, " Speedhack");
            GUILayout.Label("Extra speed " + _speedAmount.ToString("F0"));
            _speedAmount = GUILayout.HorizontalSlider(_speedAmount, 2f, 40f);
            _onTop = GUILayout.Toggle(_onTop, " Always on top (walk over geometry)");
            _bhop = GUILayout.Toggle(_bhop, " Bhop (hold Space)");
        }

        private void GunContent()
        {
            _noRecoil = GUILayout.Toggle(_noRecoil, " No recoil");
            _noSpread = GUILayout.Toggle(_noSpread, " No spread");
            _noFireDelay = GUILayout.Toggle(_noFireDelay, " Bullet Storm (kick)");
            _fastFire = GUILayout.Toggle(_fastFire, " Fire rate boost");
            GUILayout.Label("Fire rate x" + _fireMult.ToString("F1"));
            _fireMult = GUILayout.HorizontalSlider(_fireMult, 1f, 4f);
            _unlimAmmo = GUILayout.Toggle(_unlimAmmo, " Unlimited ammo (OFFLINE)");
        }

        private void MiscContent()
        {
            GUILayout.Label("-- keybinds (mouse buttons blocked) --");
            if (GUILayout.Button(_menuBindListening ? "press any key..." : "Menu: " + KeyName(_menuKey))) _menuBindListening = true;
            if (GUILayout.Button(_espKeyListen ? "press any key..." : "ESP toggle: " + KeyName(_espKey))) _espKeyListen = true;
            if (GUILayout.Button(_aimToggleListen ? "press any key..." : "Aimbot toggle: " + KeyName(_aimToggleKey))) _aimToggleListen = true;
            if (GUILayout.Button(_flyKeyListen ? "press any key..." : "Fly toggle: " + KeyName(_flyKey))) _flyKeyListen = true;
            GUILayout.Space(8f);
            if (GUILayout.Button("SAVE SETTINGS")) SaveCfg();
            if (GUILayout.Button("Close menu") && _menuOpen) ToggleMenu();
        }
        private static string KeyName(KeyCode k) { return k == KeyCode.None ? "unset" : k.ToString(); }

        // open: remember the game's cursor state; close: put it back exactly (lobby keeps its cursor, match re-locks)
        private void ToggleMenu()
        {
            _menuOpen = !_menuOpen;
            _sMenuOpen = _menuOpen;
            try
            {
                if (_menuOpen) { _prevLock = Cursor.lockState; _prevCursorVis = Cursor.visible; }
                else { Cursor.lockState = _prevLock; Cursor.visible = _prevCursorVis; }
            }
            catch { }
        }

        // GameRooms is a lobby/persistent object → FindObjectOfType misses it in-match; scan ALL (incl inactive/DontDestroy)
        private GameRooms FindGameRooms()
        {
            try { var g = Object.FindObjectOfType<GameRooms>(); if (g != null) return g; } catch { }
            try
            {
                var all = Resources.FindObjectsOfTypeAll<GameRooms>();
                if (all != null && all.Length > 0) return all[0];
            }
            catch { }
            return null;
        }

        // Diagnostic only: report top-level windows that opt out of normal screen capture.
        private void DumpWindowAffinities()
        {
            try
            {
                _affinityWindowsFound = 0;
                EnumWindows(_affinityScanCallback, IntPtr.Zero);
                LoggerInstance.Msg("WINDOW AFFINITY scan complete; flagged windows=" + _affinityWindowsFound);
            }
            catch (Exception e) { LoggerInstance.Warning("Window affinity scan: " + e.Message); }
        }

        private static bool AffinityScanWindow(IntPtr hwnd, IntPtr lParam)
        {
            try
            {
                if (!IsWindowVisible(hwnd) || !GetWindowDisplayAffinity(hwnd, out uint affinity) || affinity == 0)
                    return true;

                var title = new StringBuilder(512);
                GetWindowText(hwnd, title, title.Capacity);
                GetWindowThreadProcessId(hwnd, out uint pid);
                string processName = "?";
                try { processName = Process.GetProcessById((int)pid).ProcessName; } catch { }
                string mode = affinity == 0x11 ? "EXCLUDEFROMCAPTURE" : affinity == 0x1 ? "MONITOR" : "UNKNOWN";
                _affinityWindowsFound++;
                MelonLogger.Msg("WINDOW AFFINITY hwnd=0x" + hwnd.ToInt64().ToString("X") + " pid=" + pid
                    + " process=" + processName + " mode=" + mode + " value=0x" + affinity.ToString("X")
                    + " title='" + title + "'");
            }
            catch { }
            return true;
        }

        // dev: dump the current room list — checks whether the server leaks real passwords to clients
        private void DumpRooms()
        {
            try
            {
                var gr = FindGameRooms();
                if (gr == null) { LoggerInstance.Msg("ROOMS: no GameRooms instance in scene"); return; }
                var list = gr.gameRooms;
                if (list == null) { LoggerInstance.Msg("ROOMS: gameRooms is null"); return; }
                int n = list.Count;
                LoggerInstance.Msg("======== ROOMS DUMP (F6) ======== count=" + n);
                var vals = list.Values;
                var arr = new Il2CppReferenceArray<Il2Cpp.GameRooms.GameRoomInfo>(n);
                vals.CopyTo(arr, 0);
                for (int i = 0; i < n && i < 60; i++)
                {
                    var r = arr[i]; if (r == null) continue;
                    LoggerInstance.Msg("  room name='" + r.matchName + "' map=" + r.map + " mode=" + r.gamemode
                        + " players=" + r.playerCount + "/" + r.maxPlayers + " private=" + r.isPrivate + " pwProt=" + r.passwordProtected
                        + " PW='" + r.password + "' addr=" + r.address + ":" + r.port + " bots=" + r.wantsBots + " ver=" + r.version);
                }
            }
            catch (Exception e) { LoggerInstance.Warning("DumpRooms: " + e.Message); }
        }

        private static string TPath(Transform t)
        {
            try { string s = t.name; var p = t.parent; int g = 0; while (p != null && g < 6) { s = p.name + "/" + s; p = p.parent; g++; } return s; } catch { return "?"; }
        }

        private void Dump()
        {
            try
            {
                LoggerInstance.Msg("======== LIVE DUMP (F7) ========  players=" + _players.Length + " local=" + (_local != null));
                // ---- cameras ----
                var cams = Camera.allCameras;
                LoggerInstance.Msg("Camera.main=" + (Camera.main != null ? Camera.main.name : "NULL") + "  allCameras=" + cams.Length);
                for (int i = 0; i < cams.Length; i++)
                {
                    var c = cams[i]; if (c == null) continue;
                    LoggerInstance.Msg("  cam '" + c.name + "' depth=" + c.depth + " fov=" + c.fieldOfView.ToString("F0") + " enabled=" + c.enabled + " pos=" + c.transform.position.ToString("F1") + " fwd=" + c.transform.forward.ToString("F2") + " mask=" + c.cullingMask);
                }
                var pk = PickCamera();
                LoggerInstance.Msg("PickCamera -> " + (pk != null ? pk.name + " pos=" + pk.transform.position.ToString("F1") : "null"));
                if (_local != null)
                {
                    try { var ct = _local.cameraTransform; LoggerInstance.Msg("local.cameraTransform pos=" + (ct != null ? ct.position.ToString("F1") : "null")); } catch { }
                    try { var g = _local.mpGunScript; LoggerInstance.Msg("local.mpGunScript=" + (g != null ? ("'" + g.name + "' ammo=" + g.ammo + " total=" + g.totalAmmo) : "NULL")); } catch (Exception e) { LoggerInstance.Msg("mpGunScript EX " + e.Message); }
                    // find EVERY GunScript in the scene, flag which belongs to us
                    try
                    {
                        var guns = Object.FindObjectsOfType<GunScript>();
                        LoggerInstance.Msg("GunScripts in scene=" + guns.Length);
                        for (int i = 0; i < guns.Length && i < 10; i++)
                        {
                            var g = guns[i]; if (g == null) continue;
                            bool mine = false; try { var ps = g.GetComponentInParent<PlayerScript>(); mine = ps != null && ps.isMine; } catch { }
                            LoggerInstance.Msg("  gun[" + i + "] '" + g.name + "' ammo=" + g.ammo + " total=" + g.totalAmmo + " enabled=" + g.enabled + " MINE=" + mine);
                        }
                    }
                    catch (Exception e) { LoggerInstance.Msg("guns EX " + e.Message); }
                    // ---- LOCAL BODY scan: where is my 3rd-person model? ----
                    try
                    {
                        Transform gvTf = null; try { var gv = _local.fpsGunView; if (gv != null) gvTf = gv.transform; } catch { }
                        LoggerInstance.Msg("-- LOCAL body scan --  fpsGunView=" + (gvTf != null ? gvTf.name : "NULL"));
                        // every SkinnedMeshRenderer under _local INCLUDING inactive - this is where a hidden body would be
                        var smrsL = _local.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                        LoggerInstance.Msg("  SMRs under _local (incl inactive) = " + smrsL.Length);
                        for (int i = 0; i < smrsL.Length && i < 30; i++)
                        {
                            var s = smrsL[i]; if (s == null) continue;
                            bool underGun = gvTf != null && s.transform.IsChildOf(gvTf);
                            LoggerInstance.Msg("   SMR '" + s.name + "' en=" + s.enabled + " active=" + s.gameObject.activeInHierarchy + " gun=" + underGun + " layer=" + s.gameObject.layer + " path=" + TPath(s.transform));
                        }
                        // also any child GameObject whose name hints a body (Soldier/Blayze/Body/Ragdoll/Mesh), even inactive
                        var tfs = _local.GetComponentsInChildren<Transform>(true);
                        LoggerInstance.Msg("  transforms under _local = " + tfs.Length + " ; body-name hits:");
                        int hits = 0;
                        for (int i = 0; i < tfs.Length; i++)
                        {
                            var t = tfs[i]; if (t == null) continue;
                            string nm = t.name.ToLower();
                            if (nm.Contains("soldier") || nm.Contains("blayze") || nm.Contains("ragdoll") || (nm.Contains("body") && !nm.Contains("playerbody")))
                            { hits++; if (hits <= 25) LoggerInstance.Msg("    HIT '" + t.name + "' active=" + t.gameObject.activeInHierarchy + " layer=" + t.gameObject.layer + " path=" + TPath(t)); }
                        }
                        LoggerInstance.Msg("  body-name hits total = " + hits);
                    }
                    catch (Exception e) { LoggerInstance.Msg("bodyscan EX " + e.Message); }
                }
                // ---- enemy nearest to crosshair ----
                PlayerScript en = null; float bestA = 999f; var pkc = pk;
                if (pkc != null)
                {
                    Vector3 cp = pkc.transform.position, cf = pkc.transform.forward;
                    for (int i = 0; i < _players.Length; i++)
                    {
                        var p = _players[i]; if (p == null || p.isMine || !IsAlive(p) || IsTeam(p)) continue;
                        var tr = p.transform; if (tr == null || tr.position.y < -100f) continue;
                        float a = Vector3.Angle(cf, tr.position - cp);
                        if (a < bestA) { bestA = a; en = p; }
                    }
                }
                if (en != null)
                {
                    var etr = en.transform;
                    LoggerInstance.Msg("ENEMY nearest-crosshair: name='" + etr.name + "' transform.pos=" + etr.position.ToString("F2") + " goActive=" + etr.gameObject.activeInHierarchy + " angleFromCross=" + bestA.ToString("F1"));
                    try
                    {
                        int lodc = 0; try { lodc = en.GetComponentsInChildren<LODGroup>(true).Length; } catch { }
                        var smrs = en.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                        LoggerInstance.Msg("  skinnedRenderers=" + smrs.Length + " LODGroups=" + lodc);
                        for (int i = 0; i < smrs.Length && i < 12; i++)
                        {
                            var s = smrs[i]; if (s == null) continue;
                            LoggerInstance.Msg("   smr[" + i + "] '" + s.name + "' en=" + s.enabled + " vis=" + s.isVisible + " forceOff=" + s.forceRenderingOff);
                        }
                    }
                    catch (Exception e) { LoggerInstance.Msg("  smr EX " + e.Message); }
                    // head bone
                    try
                    {
                        var ts = en.GetComponentsInChildren<Transform>(true);
                        for (int i = 0; i < ts.Length; i++) { var t = ts[i]; if (t != null && t.name.ToLower().Contains("head")) { LoggerInstance.Msg("   HEAD bone '" + t.name + "' pos=" + t.position.ToString("F2")); break; } }
                    }
                    catch { }
                }
            }
            catch (Exception e) { LoggerInstance.Warning("Dump: " + e.Message); }
        }
    }
}
