using System;
using System.Collections;
using System.Collections.Generic;
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
        private float _baseHordeDuration = 25f;

        // ───── HUD 배너 ("무언가 다가옵니다!") ─────
        private float _hordeMessageUntil;
        private GUIStyle _bannerStyle;
        private GUIStyle _bannerOutlineStyle;

        // ───── 적 스캔 진행 HUD ─────
        private float _scanUiStartTime;
        private float _scanUiDuration = 1.2f;   // 스캔 퍼센트 애니메이션 시간
        private int _scanEnemyCount;
        private bool _scanUiActive;
        private GUIStyle _scanStyle;
        private GUIStyle _scanMaxStyle;         // 100% / 준비완료용 (하늘색)

        // “준비 완료” 판단용
        private int _maxDetectedEnemies;
        private float _lastIncreaseTime;
        private const float ReadyDelaySeconds = 5f;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Debug.Log("[HordeMode] HordeModeController Awake 완료");
        }

        private void Update()
        {
            // 호드 안 돌 때만 주기적으로 스캔
            if (!_hordeActive && Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + ScanInterval;
                RescanCharacters();
            }

            // F7 = 호드 발동
            if (Input.GetKeyDown(KeyCode.F7))
            {
                StartCoroutine(TriggerHordeWave());
            }
        }

        private void LateUpdate()
        {
            if (!_hordeActive)
                return;

            if (Time.time > _hordeEndTime)
            {
                _hordeActive = false;
                return;
            }

            DoHordeChaseStep();
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint &&
                Event.current.type != EventType.Layout)
                return;

            if (_bannerStyle == null)
                SetupGuiStyle();

            // ── 호드 경고 배너 ("무언가 다가옵니다!") ──
            if (Time.time <= _hordeMessageUntil)
            {
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

            // ── 적 스캔 진행 퍼센트 ──
            if (_scanUiActive && _scanStyle != null)
            {
                float elapsed = Time.time - _scanUiStartTime;
                if (elapsed < 0f || elapsed > _scanUiDuration)
                {
                    _scanUiActive = false;
                }
                else
                {
                    float t = elapsed / _scanUiDuration;
                    if (t < 0f) t = 0f;
                    if (t > 1f) t = 1f;

                    float percent = t * 100f;
                    string scanText = string.Format(
                        "적 스캔 중... {0:0}% (감지된 적 {1}명)",
                        percent, _scanEnemyCount
                    );

                    float scanWidth = 400f;
                    float scanX = Screen.width - scanWidth - 40f;  // 오른쪽에서 40px 안쪽
                    float scanY = 40f;

                    Rect scanRect = new Rect(scanX, scanY, scanWidth, 30f);

                    GUIStyle styleToUse = _scanStyle;
                    if (t >= 1f && _scanMaxStyle != null)
                        styleToUse = _scanMaxStyle;

                    GUI.Label(scanRect, scanText, styleToUse);
                }
            }

            // ── 감지된 적 수가 더 이상 안 오르면 "준비 완료" ──
            bool ready =
                !_hordeActive &&
                _maxDetectedEnemies > 0 &&
                _lastIncreaseTime > 0f &&
                (Time.time - _lastIncreaseTime) >= ReadyDelaySeconds;

            if (ready)
            {
                string readyText = string.Format(
                    "감지된 적 {0}명 (준비 완료)",
                    _maxDetectedEnemies
                );

                float width = 400f;
                float x = Screen.width - width - 40f;
                float y = 70f; // 스캔 퍼센트 아래 줄

                Rect rect = new Rect(x, y, width, 30f);
                GUIStyle style = _scanMaxStyle ?? _scanStyle;
                GUI.Label(rect, readyText, style);
            }
        }

        private void SetupGuiStyle()
        {
            if (_bannerStyle == null)
            {
                _bannerStyle = new GUIStyle(GUI.skin.label);
                _bannerStyle.alignment = TextAnchor.MiddleCenter;
                _bannerStyle.fontSize = 28;
                _bannerStyle.fontStyle = FontStyle.Bold;
                _bannerStyle.normal.textColor = Color.red;
                _bannerStyle.padding = new RectOffset(8, 8, 4, 4);
            }

            if (_bannerOutlineStyle == null)
            {
                _bannerOutlineStyle = new GUIStyle(_bannerStyle);
                _bannerOutlineStyle.normal.textColor = Color.black;
            }

            if (_scanStyle == null)
            {
                _scanStyle = new GUIStyle(GUI.skin.label);
                _scanStyle.alignment = TextAnchor.UpperLeft;
                _scanStyle.fontSize = 16;
                _scanStyle.fontStyle = FontStyle.Normal;
                _scanStyle.normal.textColor = Color.yellow;
                _scanStyle.padding = new RectOffset(4, 4, 4, 4);
            }

            if (_scanMaxStyle == null)
            {
                _scanMaxStyle = new GUIStyle(_scanStyle);
                _scanMaxStyle.normal.textColor = Color.cyan;   // 하늘색
            }
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

        // 호드 허용 맵
        private bool IsHordeAllowedMap()
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

        // ───── 캐릭터 스캔 (CharacterMainControl 직통) ─────
        private void RescanCharacters()
        {
            try
            {
                _enemies.Clear();
                _playerTransform = null;

                // 스캔 HUD 시작
                _scanUiStartTime = Time.time;
                _scanUiActive = true;
                _scanEnemyCount = 0;

                CharacterMainControl main = null;

                // 메인 캐릭터 (플레이어)
                try
                {
                    main = CharacterMainControl.Main;
                }
                catch (Exception ex)
                {
                    Debug.Log("[HordeMode] CharacterMainControl.Main 접근 예외: " + ex);
                }

                if (main != null && main.transform != null)
                {
                    _playerTransform = main.transform;
                }

                // 플레이어 못찾으면 카메라 루트로 폴백
                if (_playerTransform == null)
                {
                    try
                    {
                        Camera cam = Camera.main;
                        if (cam != null && cam.transform != null)
                        {
                            _playerTransform = cam.transform.root;
                        }
                    }
                    catch
                    {
                    }
                }

                CharacterMainControl[] allChars = null;
                try
                {
                    allChars = UnityEngine.Object.FindObjectsOfType<CharacterMainControl>(true);
                }
                catch (Exception ex)
                {
                    Debug.Log("[HordeMode] FindObjectsOfType<CharacterMainControl> 예외: " + ex);
                    allChars = null;
                }

                int enemies = 0;
                int total = 0;

                if (allChars != null)
                {
                    // 혹시 Main이 배열 안에 있으면 그 transform을 확실히 플레이어로 사용
                    if (main != null)
                    {
                        for (int i = 0; i < allChars.Length; i++)
                        {
                            CharacterMainControl c = allChars[i];
                            if (c == null) continue;
                            if (c == main && c.transform != null)
                            {
                                _playerTransform = c.transform;
                                break;
                            }
                        }
                    }

                    for (int i = 0; i < allChars.Length; i++)
                    {
                        CharacterMainControl c = allChars[i];
                        if (c == null) continue;

                        Transform tr = c.transform;
                        if (tr == null) continue;

                        total++;

                        // 플레이어 자신은 패스
                        if (main != null && c == main)
                            continue;

                        // 같은 팀(아군) 패스
                        if (main != null)
                        {
                            try
                            {
                                if (c.Team == main.Team)
                                    continue;
                            }
                            catch
                            {
                            }
                        }

                        // 죽은 애 패스
                        try
                        {
                            if (c.Health != null && c.Health.IsDead)
                                continue;
                        }
                        catch
                        {
                        }

                        // 펫 이름 제외 ("pet" 들어가면 스킵)
                        string goName = (tr.gameObject != null ? tr.gameObject.name : null);
                        if (!string.IsNullOrEmpty(goName))
                        {
                            string lower = goName.ToLowerInvariant();
                            if (lower.Contains("pet"))
                                continue;
                        }

                        _enemies.Add(tr);
                        enemies++;
                    }
                }

                _scanEnemyCount = enemies;

                if (_scanEnemyCount <= 0)
                {
                    _maxDetectedEnemies = 0;
                    _lastIncreaseTime = 0f;
                }
                else if (_scanEnemyCount > _maxDetectedEnemies)
                {
                    _maxDetectedEnemies = _scanEnemyCount;
                    _lastIncreaseTime = Time.time;
                }

                string scene = GetCurrentSceneName();
                Debug.Log("[HordeMode] 캐릭터 스캔 완료 (scene=" + scene +
                          ") - characters=" + (allChars != null ? allChars.Length : 0) +
                          ", enemies=" + enemies +
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
            if (!IsHordeAllowedMap())
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
