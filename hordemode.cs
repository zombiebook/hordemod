using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace hordemode
{
    // Duckov 로더 진입점:
    //   hordemode.ModBehaviour
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                GameObject go = new GameObject("HordeModeRoot");
                UnityEngine.Object.DontDestroyOnLoad(go);

                go.AddComponent<HordeModeController>();

                Debug.Log("[HordeMode] ModBehaviour.OnAfterSetup - HordeModeController 초기화 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[HordeMode] 초기화 예외: " + ex);
            }
        }
    }

    internal class HordeModeController : MonoBehaviour
    {
        // ───── 플레이어 / 적 목록 ─────
        private Transform _playerTransform;
        private readonly List<Transform> _enemies = new List<Transform>();

        private float _nextScanTime;
        private const float ScanInterval = 3f;

        // ───── 호드 파라미터 ─────
        private bool _hordeActive;
        private float _hordeEndTime;
        private const float HordeSpeed = 9f;
        private const float HordeStopDistance = 2.5f;
        // 기본 호드 시간
        private float _baseHordeDuration = 25f;

        // ───── HUD 배너 ─────
        private float _hordeMessageUntil;
        private GUIStyle _bannerStyle;
        private GUIStyle _bannerOutlineStyle;

        // ───── EnemyTracker 리플렉션용 ─────
        private bool _trackerBound;
        private bool _trackerTried;
        private Type _trackerType;
        private MethodInfo _getLiveEnemiesMethod;
        private Type _enemyInfoType;
        private FieldInfo _enemyInfoCharacterField;

        // ───── CharacterMainControl(Main) 리플렉션용 ─────
        private bool _playerTypeBound;
        private bool _playerTypeTried;
        private Type _characterMainType;
        private FieldInfo _characterMainField;
        private PropertyInfo _characterMainProperty;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Debug.Log("[HordeMode] HordeModeController Awake 완료");
        }

        private void Update()
        {
            // 호드 아닐 때만 주기 스캔
            if (!_hordeActive && Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + ScanInterval;
                RescanCharacters();
            }

            // F7 = 호드 트리거
            if (Input.GetKeyDown(KeyCode.F7))
            {
                StartCoroutine(TriggerHordeWave());
            }
        }

        private void LateUpdate()
        {
            if (_hordeActive)
            {
                if (Time.time > _hordeEndTime)
                {
                    _hordeActive = false;
                }
                else
                {
                    DoHordeChaseStep();
                }
            }
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint &&
                Event.current.type != EventType.Layout)
                return;

            if (Time.time > _hordeMessageUntil)
                return;

            if (_bannerStyle == null)
                SetupGuiStyle();

            string text = "무언가 다가옵니다!";

            float width = 600f;
            float height = 60f;
            float x = (Screen.width - width) / 2f;
            float y = 80f;

            Rect rect = new Rect(x, y, width, height);

            // 그림자
            if (_bannerOutlineStyle != null)
            {
                Rect shadow = rect;
                shadow.x += 2f;
                shadow.y += 2f;
                GUI.Label(shadow, text, _bannerOutlineStyle);
            }

            // 본문
            GUI.Label(rect, text, _bannerStyle);
        }

        private void SetupGuiStyle()
        {
            _bannerStyle = new GUIStyle(GUI.skin.label);
            _bannerStyle.alignment = TextAnchor.MiddleCenter;
            _bannerStyle.fontSize = 28;
            _bannerStyle.fontStyle = FontStyle.Bold;
            _bannerStyle.normal.textColor = Color.red;
            _bannerStyle.padding = new RectOffset(8, 8, 4, 4);

            _bannerOutlineStyle = new GUIStyle(_bannerStyle);
            _bannerOutlineStyle.normal.textColor = Color.black;
        }

        // ───── 씬 / 맵 이름 ─────

        private string GetCurrentSceneName()
        {
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                return scene.name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // 호드 허용 맵 판별
        private bool IsZeroZone()
        {
            string sceneName = GetCurrentSceneName();
            if (string.IsNullOrEmpty(sceneName))
                return false;

            string lower = sceneName.ToLowerInvariant();

            // 제로존 : Level_GroundZero_*
            if (lower.Contains("groundzero"))
                return true;

            // 벙커/기지 : Base 계열
            if (lower.Contains("base"))
                return true;

            // 창고 구역 : Level_HiddenWarehouse
            if (lower.Contains("hiddenwarehouse"))
                return true;

            // 농장마을 남부 : Level_Farm_01
            if (lower.Contains("farm_01"))
                return true;

            // 농장마을 : Level_Farm_Main
            if (lower.Contains("farm_main"))
                return true;

            // J-Lab 연구소 : JLab_1 및 level_jlab*
            if (lower.Contains("jlab"))
                return true;

            // J-Lab 연구소 입구 : Farm_JLab_Facility
            if (lower.Contains("farm_jlab_facility"))
                return true;

            // 폭풍 구역 : StormZone / Level_StormZone_*
            if (lower.Contains("stormzone"))
                return true;

            // 위 조건에 안 걸리면 호드 비활성 맵
            return false;
        }

        // 맵별 호드 지속 시간
        private float GetHordeDurationForScene()
        {
            float duration = _baseHordeDuration;

            string sceneName = GetCurrentSceneName();
            if (string.IsNullOrEmpty(sceneName))
                return duration;

            string lower = sceneName.ToLowerInvariant();

            // 농장마을 남부 / 농장마을 : 넓어서 오래 추격
            if (lower.Contains("farm_01") || lower.Contains("farm_main"))
                return 45f;

            // 폭풍 구역: 약간 더 길게
            if (lower.Contains("stormzone"))
                return 40f;

            // 창고 구역: 중간 정도
            if (lower.Contains("hiddenwarehouse"))
                return 30f;

            return duration;
        }

        // ───── CharacterMainControl.Main 리플렉션 ─────

        private bool TryBindCharacterMain()
        {
            if (_playerTypeBound)
                return true;

            if (_playerTypeTried && _characterMainType == null)
                return false;

            _playerTypeTried = true;

            try
            {
                // 1차: 이름으로 바로 찾기
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int ai = 0; ai < asms.Length; ai++)
                {
                    Assembly asm = asms[ai];
                    if (asm == null) continue;

                    Type t = asm.GetType("CharacterMainControl");
                    if (t == null)
                    {
                        t = asm.GetType("Duckov.CharacterMainControl");
                    }

                    if (t != null)
                    {
                        _characterMainType = t;
                        break;
                    }
                }

                // 2차: 타입 전체 스캔
                if (_characterMainType == null)
                {
                    Assembly[] asms2 = AppDomain.CurrentDomain.GetAssemblies();
                    for (int ai = 0; ai < asms2.Length && _characterMainType == null; ai++)
                    {
                        Assembly asm = asms2[ai];
                        if (asm == null) continue;

                        Type[] types;
                        try
                        {
                            types = asm.GetTypes();
                        }
                        catch
                        {
                            continue;
                        }

                        for (int ti = 0; ti < types.Length; ti++)
                        {
                            Type t = types[ti];
                            if (t == null) continue;
                            if (t.Name == "CharacterMainControl")
                            {
                                _characterMainType = t;
                                break;
                            }
                        }
                    }
                }

                if (_characterMainType == null)
                {
                    Debug.Log("[HordeMode] CharacterMainControl 타입을 찾지 못함");
                    return false;
                }

                _characterMainField = _characterMainType.GetField(
                    "Main",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                _characterMainProperty = _characterMainType.GetProperty(
                    "Main",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (_characterMainField == null && _characterMainProperty == null)
                {
                    Debug.Log("[HordeMode] CharacterMainControl.Main 필드/프로퍼티 없음");
                    return false;
                }

                _playerTypeBound = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.Log("[HordeMode] TryBindCharacterMain 예외: " + ex);
                return false;
            }
        }

        private Transform FindPlayerTransform()
        {
            try
            {
                if (TryBindCharacterMain())
                {
                    object mainObj = null;
                    if (_characterMainProperty != null)
                    {
                        mainObj = _characterMainProperty.GetValue(null, null);
                    }
                    else if (_characterMainField != null)
                    {
                        mainObj = _characterMainField.GetValue(null);
                    }

                    Component comp = mainObj as Component;
                    if (comp != null && comp.transform != null)
                    {
                        return comp.transform;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[HordeMode] FindPlayerTransform(Main) 예외: " + ex);
            }

            // 실패 시 카메라 루트로 폴백
            try
            {
                Camera cam = Camera.main;
                if (cam != null && cam.transform != null)
                {
                    return cam.transform.root;
                }
            }
            catch
            {
            }

            return null;
        }

        // ───── EnemyTracker 바인딩 ─────

        private bool TryBindEnemyTracker()
        {
            if (_trackerBound)
                return true;

            if (_trackerTried && _trackerType == null)
                return false;

            _trackerTried = true;

            try
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Assembly a = asms[i];
                    if (a == null) continue;

                    Type t = a.GetType("DuckovCheatUI.Utiles.EnemyTracker");
                    if (t != null)
                    {
                        _trackerType = t;
                        break;
                    }
                }

                if (_trackerType == null)
                {
                    Debug.Log("[HordeMode] EnemyTracker 타입을 찾지 못함 (치트 모드 필요)");
                    return false;
                }

                _getLiveEnemiesMethod = _trackerType.GetMethod(
                    "GetLiveEnemies",
                    BindingFlags.Public | BindingFlags.Static);

                if (_getLiveEnemiesMethod == null)
                {
                    Debug.Log("[HordeMode] EnemyTracker.GetLiveEnemies 메서드를 찾지 못함");
                    return false;
                }

                _enemyInfoType = _trackerType.GetNestedType(
                    "EnemyInfo",
                    BindingFlags.Public | BindingFlags.NonPublic);

                if (_enemyInfoType == null)
                {
                    Debug.Log("[HordeMode] EnemyTracker.EnemyInfo 타입을 찾지 못함");
                    return false;
                }

                _enemyInfoCharacterField = _enemyInfoType.GetField(
                    "character",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

                if (_enemyInfoCharacterField == null)
                {
                    Debug.Log("[HordeMode] EnemyInfo.character 필드를 찾지 못함");
                    return false;
                }

                _trackerBound = true;
                Debug.Log("[HordeMode] EnemyTracker 연동 완료");
                return true;
            }
            catch (Exception ex)
            {
                Debug.Log("[HordeMode] TryBindEnemyTracker 예외: " + ex);
                return false;
            }
        }

        private Transform ToTransform(object obj)
        {
            if (obj == null) return null;

            Transform tr = obj as Transform;
            if (tr != null) return tr;

            Component comp = obj as Component;
            if (comp != null) return comp.transform;

            return null;
        }

        // ───── EnemyTracker 기반 캐릭터 스캔 ─────
        private void RescanCharacters()
        {
            try
            {
                _enemies.Clear();
                _playerTransform = FindPlayerTransform();

                if (!TryBindEnemyTracker())
                {
                    Debug.Log("[HordeMode] EnemyTracker 연동 실패 - 적 스캔 불가");
                    return;
                }

                object listObj = _getLiveEnemiesMethod.Invoke(null, null);
                IEnumerable enumerable = listObj as IEnumerable;
                if (enumerable == null)
                {
                    Debug.Log("[HordeMode] EnemyTracker.GetLiveEnemies 반환값이 IEnumerable 아님");
                    return;
                }

                HashSet<Transform> added = new HashSet<Transform>();
                int liveCount = 0;

                foreach (object enemyInfoObj in enumerable)
                {
                    if (enemyInfoObj == null) continue;

                    object charObj = _enemyInfoCharacterField.GetValue(enemyInfoObj);
                    Component chComp = charObj as Component;
                    if (chComp == null) continue;

                    Transform charTransform = chComp.transform;
                    if (charTransform == null) continue;

                    liveCount++;

                    // 혹시라도 플레이어 트리랑 겹치면 방어
                    if (_playerTransform != null)
                    {
                        if (charTransform == _playerTransform ||
                            charTransform.IsChildOf(_playerTransform) ||
                            _playerTransform.IsChildOf(charTransform))
                        {
                            continue;
                        }
                    }

                    if (!added.Add(charTransform))
                        continue;

                    _enemies.Add(charTransform);
                }

                string sceneName = GetCurrentSceneName();
                Debug.Log("[HordeMode] EnemyTracker 기반 스캔 완료 (scene=" + sceneName +
                          ") - liveEnemies=" + liveCount +
                          ", uniqueEnemies=" + _enemies.Count +
                          ", player=" + (_playerTransform != null));
            }
            catch (Exception ex)
            {
                Debug.Log("[HordeMode] RescanCharacters 예외: " + ex);
            }
        }

        // ───── 호드 발동 ─────

        private IEnumerator TriggerHordeWave()
        {
            if (!IsZeroZone())
            {
                string sceneName = GetCurrentSceneName();
                Debug.Log("[HordeMode] 이 맵에서는 호드 발동이 비활성화되어 있습니다. scene=" + sceneName);
                yield break;
            }

            RescanCharacters();

            if (_playerTransform == null || _enemies.Count == 0)
            {
                Debug.Log("[HordeMode] 호드 시작 실패 - 플레이어나 적을 찾지 못함");
                yield break;
            }

            Debug.Log("[HordeMode] HORDE TRIGGERED! enemy count = " + _enemies.Count);

            _hordeMessageUntil = Time.time + 3f;

            // 약간 딜레이 후 추격 시작
            yield return new WaitForSeconds(1.5f);

            float duration = GetHordeDurationForScene();
            _hordeActive = true;
            _hordeEndTime = Time.time + duration;

            Debug.Log("[HordeMode] Horde duration = " + duration + "s (scene=" + GetCurrentSceneName() + ")");
        }

        // ───── 적 이동 ─────

        private void DoHordeChaseStep()
        {
            if (_playerTransform == null)
                return;

            Vector3 playerPos = _playerTransform.position;
            float stopSqr = HordeStopDistance * HordeStopDistance;
            float delta = Time.deltaTime * HordeSpeed;

            for (int i = 0; i < _enemies.Count; i++)
            {
                Transform et = _enemies[i];
                if (et == null)
                    continue;

                Vector3 pos = et.position;
                Vector3 dir = playerPos - pos;
                dir.y = 0f;

                float sqr = dir.sqrMagnitude;
                if (sqr <= stopSqr)
                    continue;

                if (sqr > 0.0001f)
                    dir = dir.normalized;
                else
                    dir = Vector3.zero;

                Vector3 newPos = pos + dir * delta;
                et.position = newPos;

                if (dir.sqrMagnitude > 0.0001f)
                {
                    et.forward = dir;
                }
            }
        }
    }
}
