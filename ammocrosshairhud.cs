using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.UI;

namespace ammocrosshairhud
{
    // Duckov 모드 로더 엔트리
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                Debug.Log("[AmmoCrosshairHUD] ModBehaviour.OnAfterSetup - HUD 매니저 생성");

                GameObject root = new GameObject("AmmoCrosshairHUDRoot_UI");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.AddComponent<AmmoCrosshairHUDManager>();
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] OnAfterSetup 예외: " + ex);
            }
        }

        protected override void OnBeforeDeactivate()
        {
            Debug.Log("[AmmoCrosshairHUD] ModBehaviour.OnBeforeDeactivate - 언로드");
        }
    }

    public class AmmoCrosshairHUDManager : MonoBehaviour
    {
        private static AmmoCrosshairHUDManager _instance;

        // ─────────────────────────────────────────────
        // CharacterMainControl 리플렉션
        // ─────────────────────────────────────────────
        private Type _characterMainType;
        private PropertyInfo _characterMainSingletonProp;

        // agentHolder / 현재 총
        private bool _holderReflectionCached;
        private FieldInfo _agentHolderField;
        private PropertyInfo _agentHolderProp;
        private MemberInfo _currentHoldGunMember;

        // ─────────────────────────────────────────────
        // 총 내부 (ItemSetting_Gun)
        // ─────────────────────────────────────────────
        private bool _ammoReflectionCached;
        private PropertyInfo _gunItemProp;
        private MethodInfo _getComponentMethod;
        private Type _itemSettingGunType;
        private FieldInfo _bulletCountField;
        private PropertyInfo _targetBulletIdProp;

        // 탄종 변경용
        private MethodInfo _getCurrentLoadedBulletMethod;
        private MethodInfo _setTargetBulletTypeMethod;
        private MemberInfo _preferBulletsMember;

        // ─────────────────────────────────────────────
        // 인벤토리 리플렉션
        // ─────────────────────────────────────────────
        private bool _inventoryReflectionCached;
        private MemberInfo _characterItemMember;
        private MemberInfo _inventoryMember;
        private MemberInfo _inventoryContentMember;

        // ─────────────────────────────────────────────
        // HUD 값
        // ─────────────────────────────────────────────
        private int _lastMagAmmo;
        private int _lastReserveAmmo;
        private float _lastAmmoUpdateTime;
        private float _nextScanTime;

        // ─────────────────────────────────────────────
        // AimMarker / UI
        // ─────────────────────────────────────────────
        private Component _aimMarkerComponent;
        private RectTransform _aimRight;
        private Transform _aimMarkerUIRoot;
        private RectTransform _ammoPanelRect;
        private TMPro.TextMeshProUGUI _ammoText;
        private bool _uiReady;
        private float _nextAimSearchTime;

        // 크로스헤어 기준 패널 위치 (탄약 숫자)
        private float _panelOffsetX = 160f; // 너가 찾은 최적값
        private float _panelOffsetY = 40f;

        // ─────────────────────────────────────────────
        // 리로드 상태 (gunState == 5 일 때만 떨림)
        // ─────────────────────────────────────────────
        private bool _reloadStateReflectionCached;
        private FieldInfo _gunStateField;
        private const int RELOAD_STATE_VALUE = 5;
        private bool _isReloading;

        // 총 타입별 탄창 크기(최대 장전 수)
        private readonly Dictionary<int, int> _clipSizeByGunType = new Dictionary<int, int>();
        private int _currentGunTypeId;

        // ─────────────────────────────────────────────
        // 커스텀 크로스헤어
        // ─────────────────────────────────────────────
        private RectTransform _customCrosshairRoot;
        private List<Image> _customCrosshairImages = new List<Image>();
        private float _customCrosshairScale = 2.0f;
        private Vector2 _customCrosshairOffset = Vector2.zero; // AimMarker 기준 그대로 위치

        private readonly List<Graphic> _originalCrosshairGraphics = new List<Graphic>();

        // ─────────────────────────────────────────────
        // 거리 기반 색 변경
        // ─────────────────────────────────────────────
        private bool _aimExtraReflectionCached;
        private MemberInfo _currentAimDirectionMember;
        private float _currentAimDistance = -1f;

        // ─────────────────────────────────────────────

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            Debug.Log("[AmmoCrosshairHUD] Awake");
        }

        private void Update()
        {
            // 1) 리로드 여부 추적
            UpdateReloadStateSimple();

            // 2) HUD 유실 체크 (맵 이동 등)
            if (_uiReady)
            {
                bool lost =
                    _aimMarkerComponent == null ||
                    _aimMarkerUIRoot == null ||
                    _aimRight == null ||
                    (_ammoPanelRect != null && _ammoPanelRect.transform == null);

                if (lost)
                {
                    Debug.Log("[AmmoCrosshairHUD] HUD 참조 유실 감지 - 재설정 준비");

                    _uiReady = false;
                    _aimMarkerComponent = null;
                    _aimMarkerUIRoot = null;
                    _aimRight = null;
                    _ammoPanelRect = null;
                    _ammoText = null;
                    _customCrosshairRoot = null;
                    _customCrosshairImages.Clear();
                    _originalCrosshairGraphics.Clear();
                    _aimExtraReflectionCached = false;
                    _currentAimDirectionMember = null;
                    _currentAimDistance = -1f;
                    _nextAimSearchTime = 0f;
                }
            }

            // 3) AimMarker 기반 UI 세팅
            if (!_uiReady && Time.time >= _nextAimSearchTime)
            {
                _nextAimSearchTime = Time.time + 3f;
                TrySetupUI();
            }

            // 4) 우클릭 조준 중 Q → 탄종 변경
            if (Input.GetMouseButton(1) && Input.GetKeyDown(KeyCode.Q))
            {
                object mainObjForSwitch = GetMainCharacter();
                if (mainObjForSwitch != null)
                {
                    object gunObjForSwitch = GetCurrentGun(mainObjForSwitch);
                    if (gunObjForSwitch != null && !_isReloading)
                    {
                        TryCycleBulletType(mainObjForSwitch, gunObjForSwitch);
                    }
                }
            }

            // 5) 일정 주기로 탄약 스캔
            if (_uiReady && Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + 0.10f;
                SafeUpdateAmmo();
            }

            // 6) HUD 갱신 + 거리 기반 색 변경
            if (_uiReady)
            {
                if (_ammoPanelRect != null && _ammoText != null)
                {
                    UpdateAmmoUI();
                }

                if (_customCrosshairRoot != null)
                {
                    UpdateCustomCrosshairPosition();
                }

                UpdateAimDistanceColor();
            }
        }

        // ─────────────────────────────────────────────
        // AimMarker / DistanceIndicator 기반 UI 세팅
        // ─────────────────────────────────────────────
        private void TrySetupUI()
        {
            try
            {
                if (_uiReady)
                    return;

                GameObject hudCanvas = GameObject.Find("HUDCanvas");
                if (hudCanvas == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] HUDCanvas 를 찾지 못했습니다.");
                    return;
                }

                // AimMarker 컴포넌트를 타입 이름으로 탐색
                Component[] comps = hudCanvas.GetComponentsInChildren<Component>(true);
                Component found = null;
                for (int i = 0; i < comps.Length; i++)
                {
                    Component c = comps[i];
                    if (c == null) continue;
                    Type t = c.GetType();
                    if (t != null && t.Name == "AimMarker")
                    {
                        found = c;
                        break;
                    }
                }

                if (found == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] AimMarker 컴포넌트를 찾지 못했습니다.");
                    return;
                }

                _aimMarkerComponent = found;
                Transform aimTr = _aimMarkerComponent.transform;

                // 기존 거리 텍스트 템플릿: AimMarker/DistanceIndicator/Background/Text
                Transform distanceTextTr = aimTr.Find("DistanceIndicator/Background/Text");
                if (distanceTextTr == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] DistanceIndicator/Background/Text 경로를 찾지 못했습니다.");
                    return;
                }

                RectTransform backgroundTemplate = distanceTextTr.parent as RectTransform;
                if (backgroundTemplate == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] DistanceIndicator/Background RectTransform 을 찾지 못했습니다.");
                    return;
                }

                // AimMarker.right, AimMarker.aimMarkerUI 리플렉션
                Type aimType = _aimMarkerComponent.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                object rightObj = null;
                FieldInfo rightField = aimType.GetField("right", flags);
                if (rightField != null)
                {
                    rightObj = rightField.GetValue(_aimMarkerComponent);
                }
                else
                {
                    PropertyInfo rightProp = aimType.GetProperty("right", flags) ??
                                             aimType.GetProperty("Right", flags);
                    if (rightProp != null)
                        rightObj = rightProp.GetValue(_aimMarkerComponent, null);
                }

                _aimRight = rightObj as RectTransform;
                if (_aimRight == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] AimMarker.right RectTransform 을 얻지 못했습니다.");
                    return;
                }

                object uiRootObj = null;
                FieldInfo uiField = aimType.GetField("aimMarkerUI", flags);
                if (uiField != null)
                {
                    uiRootObj = uiField.GetValue(_aimMarkerComponent);
                }
                else
                {
                    PropertyInfo uiProp = aimType.GetProperty("aimMarkerUI", flags) ??
                                          aimType.GetProperty("AimMarkerUI", flags);
                    if (uiProp != null)
                        uiRootObj = uiProp.GetValue(_aimMarkerComponent, null);
                }

                _aimMarkerUIRoot = uiRootObj as Transform;
                if (_aimMarkerUIRoot == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] AimMarker.aimMarkerUI Transform 을 얻지 못했습니다.");
                    return;
                }

                // ─────────────────────────────────────
                // 탄약 패널 생성 (Distance 배경 복제)
                // ─────────────────────────────────────
                GameObject panelObj = UnityEngine.Object.Instantiate(
                    backgroundTemplate.gameObject,
                    _aimMarkerUIRoot
                );

                _ammoPanelRect = panelObj.GetComponent<RectTransform>();
                if (_ammoPanelRect == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] 복제된 패널에 RectTransform 이 없습니다.");
                    return;
                }

                _ammoPanelRect.name = "AmmoHUDPanel (Modded)";
                _ammoPanelRect.anchorMin = new Vector2(0.5f, 0.5f);
                _ammoPanelRect.anchorMax = new Vector2(0.5f, 0.5f);
                _ammoPanelRect.pivot = new Vector2(1f, 0.5f);

                Transform textChild = _ammoPanelRect.transform.Find("Text");
                if (textChild == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] AmmoHUDPanel 내에 'Text' 자식을 찾지 못했습니다.");
                    return;
                }

                _ammoText = textChild.GetComponent<TMPro.TextMeshProUGUI>();
                if (_ammoText == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] AmmoHUDPanel.Text 에 TextMeshProUGUI 가 없습니다.");
                    return;
                }

                _ammoText.text = string.Empty;
                _ammoText.enableWordWrapping = false;
                _ammoText.alignment = TMPro.TextAlignmentOptions.Left;

                _ammoPanelRect.gameObject.SetActive(false);

                // ─────────────────────────────────────
                // 커스텀 크로스헤어 생성
                // ─────────────────────────────────────
                CreateCustomCrosshairRoot();

                // ─────────────────────────────────────
                // 기존 AimMarker 크로스헤어 그래픽 숨김
                // ─────────────────────────────────────
                HideOriginalCrosshairGraphics();

                _uiReady = true;
                Debug.Log("[AmmoCrosshairHUD] AimMarker 기반 탄약/크로스헤어 UI 설정 완료.");
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] TrySetupUI 예외: " + ex);
            }
        }

        // ─────────────────────────────────────────────
        // 커스텀 크로스헤어 생성/위치/색상
        // ─────────────────────────────────────────────
        private void CreateCustomCrosshairRoot()
        {
            try
            {
                if (_customCrosshairRoot != null)
                    return;
                if (_aimMarkerUIRoot == null)
                    return;

                GameObject rootObj = new GameObject("CustomCrosshairRoot", typeof(RectTransform));
                _customCrosshairRoot = rootObj.GetComponent<RectTransform>();
                _customCrosshairRoot.SetParent(_aimMarkerUIRoot, false);
                _customCrosshairRoot.anchorMin = new Vector2(0.5f, 0.5f);
                _customCrosshairRoot.anchorMax = new Vector2(0.5f, 0.5f);
                _customCrosshairRoot.pivot = new Vector2(0.5f, 0.5f);
                _customCrosshairRoot.sizeDelta = new Vector2(32f, 32f);
                _customCrosshairRoot.localScale = Vector3.one * _customCrosshairScale;

                _customCrosshairImages = new List<Image>();

                // 4갈래 십자선
                CreateCrosshairLine(new Vector2(-8f, 0f), new Vector2(-24f, 0f));  // 좌
                CreateCrosshairLine(new Vector2(8f, 0f), new Vector2(24f, 0f));    // 우
                CreateCrosshairLine(new Vector2(0f, 8f), new Vector2(0f, 24f));    // 위
                CreateCrosshairLine(new Vector2(0f, -8f), new Vector2(0f, -24f));  // 아래

                ApplyCrosshairColor(Color.green);
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] CreateCustomCrosshairRoot 예외: " + ex);
            }
        }

        private void CreateCrosshairLine(Vector2 inner, Vector2 outer)
        {
            if (_customCrosshairRoot == null)
                return;

            GameObject go = new GameObject("CrosshairLine", typeof(RectTransform), typeof(Image));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(_customCrosshairRoot, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            Vector2 dir = (outer - inner);
            float length = dir.magnitude;
            rect.sizeDelta = new Vector2(2f, length);

            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
            rect.localRotation = Quaternion.Euler(0f, 0f, angle);
            rect.anchoredPosition = (inner + outer) * 0.5f;

            Image img = go.GetComponent<Image>();
            img.color = Color.green;
            img.raycastTarget = false;

            _customCrosshairImages.Add(img);
        }

        private void UpdateCustomCrosshairPosition()
        {
            if (_customCrosshairRoot == null)
                return;

            // AimMarkerUI 루트의 정중앙 = 원래 크로스헤어 위치
            // _customCrosshairOffset 으로 미세 조정할 수 있음(지금은 (0,0))
            _customCrosshairRoot.anchoredPosition = _customCrosshairOffset;
            _customCrosshairRoot.localScale = Vector3.one * _customCrosshairScale;
        }


        private void HideOriginalCrosshairGraphics()
        {
            try
            {
                _originalCrosshairGraphics.Clear();

                if (_aimMarkerUIRoot == null)
                    return;

                Graphic[] graphics = _aimMarkerUIRoot.GetComponentsInChildren<Graphic>(true);
                for (int i = 0; i < graphics.Length; i++)
                {
                    Graphic g = graphics[i];
                    if (g == null) continue;

                    // 우리 탄약 패널/텍스트는 제외
                    if (_ammoPanelRect != null &&
                        (g.gameObject == _ammoPanelRect.gameObject ||
                         g.transform.IsChildOf(_ammoPanelRect.transform)))
                        continue;

                    // 커스텀 크로스헤어 선들도 제외
                    if (_customCrosshairImages != null && _customCrosshairImages.Contains(g as Image))
                        continue;

                    g.enabled = false;
                    _originalCrosshairGraphics.Add(g);
                }

                Debug.Log("[AmmoCrosshairHUD] 기존 AimMarker 크로스헤어 그래픽 숨김: " + _originalCrosshairGraphics.Count);
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] HideOriginalCrosshairGraphics 예외: " + ex);
            }
        }

        private void ApplyCrosshairColor(Color col)
        {
            if (_customCrosshairImages == null)
                return;

            for (int i = 0; i < _customCrosshairImages.Count; i++)
            {
                var img = _customCrosshairImages[i];
                if (img != null)
                {
                    img.color = col;
                }
            }
        }

        // ─────────────────────────────────────────────
        // 거리 계산 + 색 변경 (1~9 초록, 10~17 노랑, 18↑ 빨강)
        // ─────────────────────────────────────────────
        private void CacheAimReflection(object mainObj)
        {
            try
            {
                Type t = mainObj.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                PropertyInfo pDir = t.GetProperty("CurrentAimDirection", flags) ??
                                    t.GetProperty("currentAimDirection", flags);
                FieldInfo fDir = t.GetField("CurrentAimDirection", flags) ??
                                 t.GetField("currentAimDirection", flags);

                _currentAimDirectionMember = (MemberInfo)pDir ?? (MemberInfo)fDir;

                if (_currentAimDirectionMember == null)
                    Debug.Log("[AmmoCrosshairHUD] CurrentAimDirection 멤버를 CharacterMainControl에서 찾지 못했습니다.");
                else
                    Debug.Log("[AmmoCrosshairHUD] CurrentAimDirection 멤버 감지: " + _currentAimDirectionMember.Name);

                _aimExtraReflectionCached = true;
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] CacheAimReflection 예외: " + ex);
                _aimExtraReflectionCached = true;
            }
        }

        private Vector3 GetCurrentAimDirection(object mainObj)
        {
            try
            {
                if (!_aimExtraReflectionCached)
                    CacheAimReflection(mainObj);

                if (_currentAimDirectionMember == null)
                    return Vector3.zero;

                object dirObj = GetMemberValue(mainObj, _currentAimDirectionMember);
                if (dirObj is Vector3 v)
                    return v;
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] GetCurrentAimDirection 예외: " + ex);
            }
            return Vector3.zero;
        }

        private void UpdateAimDistanceColor()
        {
            try
            {
                if (_customCrosshairImages == null || _customCrosshairImages.Count == 0)
                    return;

                object mainObj = GetMainCharacter();
                if (mainObj == null)
                {
                    _currentAimDistance = -1f;
                    ApplyCrosshairColor(Color.green);
                    return;
                }

                // 총을 들고 있을 때만
                object gunObj = GetCurrentGun(mainObj);
                if (gunObj == null)
                {
                    _currentAimDistance = -1f;
                    ApplyCrosshairColor(Color.green);
                    return;
                }

                Vector3 dir = GetCurrentAimDirection(mainObj);
                if (dir.sqrMagnitude < 0.0001f)
                {
                    _currentAimDistance = -1f;
                    ApplyCrosshairColor(Color.green);
                    return;
                }
                dir.Normalize();

                Component mainComp = mainObj as Component;
                if (mainComp == null)
                {
                    _currentAimDistance = -1f;
                    ApplyCrosshairColor(Color.green);
                    return;
                }

                Vector3 origin = mainComp.transform.position + Vector3.up * 1.4f; // 대략 눈높이
                float maxDistance = 100f;
                RaycastHit hit;
                float dist;

                if (Physics.Raycast(origin, dir, out hit, maxDistance))
                {
                    dist = hit.distance;
                }
                else
                {
                    dist = maxDistance;
                }

                _currentAimDistance = dist;

                Color col;
                if (dist <= 9f)
                    col = Color.green;
                else if (dist <= 17f)
                    col = Color.yellow;
                else
                    col = Color.red;

                ApplyCrosshairColor(col);
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] UpdateAimDistanceColor 예외: " + ex);
            }
        }

        // ─────────────────────────────────────────────
        // 리로드 여부 추적 (gunState == 5)
        // ─────────────────────────────────────────────
        private void UpdateReloadStateSimple()
        {
            try
            {
                object mainObj = GetMainCharacter();
                if (mainObj == null)
                {
                    _isReloading = false;
                    _currentGunTypeId = 0;
                    return;
                }

                object gunObj = GetCurrentGun(mainObj);
                if (gunObj == null)
                {
                    _isReloading = false;
                    _currentGunTypeId = 0;
                    return;
                }

                if (!_ammoReflectionCached)
                    CacheAmmoReflection(gunObj);
                if (!_ammoReflectionCached)
                    return;

                _currentGunTypeId = GetGunItemTypeId(gunObj);

                if (!_reloadStateReflectionCached)
                {
                    _reloadStateReflectionCached = true;

                    Type gt = gunObj.GetType();
                    BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                    _gunStateField = gt.GetField("gunState", flags);

                    if (_gunStateField != null)
                    {
                        Debug.Log("[AmmoCrosshairHUD] gunState 필드 감지: " + _gunStateField.Name);
                    }
                    else
                    {
                        Debug.Log("[AmmoCrosshairHUD] gunState 필드를 찾지 못했습니다.");
                    }
                }

                bool nowReload = false;
                if (_gunStateField != null)
                {
                    object sVal = _gunStateField.GetValue(gunObj);
                    int gunState = Convert.ToInt32(sVal);
                    nowReload = (gunState == RELOAD_STATE_VALUE);
                }

                _isReloading = nowReload;
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] UpdateReloadStateSimple 예외: " + ex);
                _isReloading = false;
            }
        }

        private int GetGunItemTypeId(object gunObj)
        {
            try
            {
                if (_gunItemProp == null)
                    return 0;

                object itemObj = _gunItemProp.GetValue(gunObj, null);
                Item it = itemObj as Item;
                if (it != null)
                    return it.TypeID;
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] GetGunItemTypeId 예외: " + ex);
            }
            return 0;
        }

        // ─────────────────────────────────────────────
        // HUD 숫자 갱신 (실제 값 + 리로드 중 떨림)
        // ─────────────────────────────────────────────
        private void UpdateAmmoUI()
        {
            try
            {
                if (_lastAmmoUpdateTime <= 0f)
                {
                    if (_ammoPanelRect != null)
                        _ammoPanelRect.gameObject.SetActive(false);
                    return;
                }

                int showMag = _lastMagAmmo;
                int showReserve = _lastReserveAmmo;

                // 장전탄 수에 따른 색 변경
                Color ammoColor = Color.white;
                if (showMag <= 0)
                {
                    ammoColor = Color.red;
                }
                else if (_currentGunTypeId != 0)
                {
                    int maxClip;
                    if (_clipSizeByGunType.TryGetValue(_currentGunTypeId, out maxClip) && maxClip > 0)
                    {
                        float ratio = (float)showMag / maxClip;
                        if (ratio <= 0.25f)
                            ammoColor = Color.red;
                        else if (ratio <= 0.6f)
                            ammoColor = Color.yellow;
                        else
                            ammoColor = Color.white;
                    }
                }
                _ammoText.color = ammoColor;

                _ammoText.text = showMag.ToString() + " / " + showReserve.ToString();

                if (_ammoPanelRect != null)
{
    // 화면 중앙(크로스헤어 중심) 기준으로 오른쪽에 살짝 띄우기
    Vector2 basePos = Vector2.zero;
    Vector2 pos = new Vector2(
        basePos.x + _panelOffsetX,
        basePos.y + _panelOffsetY
    );

    if (_isReloading)
    {
        // 리로드 중에만 살짝 흔들리게 (위치 쉐이크)
        float shakeAmp = 4f;
        float shakeFreq = 30f;
        float t = Time.time * shakeFreq;

        float offsetX = (Mathf.PerlinNoise(t, 0.123f) - 0.5f) * 2f * shakeAmp;
        float offsetY = (Mathf.PerlinNoise(0.456f, t) - 0.5f) * 2f * shakeAmp;

        pos.x += offsetX;
        pos.y += offsetY;
    }

    _ammoPanelRect.anchoredPosition = pos;
    _ammoPanelRect.localScale = Vector3.one;
    _ammoPanelRect.gameObject.SetActive(true);
}

            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] UpdateAmmoUI 예외: " + ex);
            }
        }

        // ─────────────────────────────────────────────
        // CharacterMainControl 리플렉션
        // ─────────────────────────────────────────────
        private object GetMainCharacter()
        {
            try
            {
                if (_characterMainType == null || _characterMainSingletonProp == null)
                    CacheCharacterMainReflection();

                if (_characterMainSingletonProp == null)
                    return null;

                return _characterMainSingletonProp.GetValue(null, null);
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] GetMainCharacter 예외: " + ex);
                return null;
            }
        }

        private void CacheCharacterMainReflection()
        {
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly a = assemblies[i];
                    Type[] types;
                    try { types = a.GetTypes(); }
                    catch { types = new Type[0]; }

                    for (int j = 0; j < types.Length; j++)
                    {
                        Type t = types[j];
                        if (t != null && t.Name == "CharacterMainControl")
                        {
                            _characterMainType = t;
                            break;
                        }
                    }

                    if (_characterMainType != null)
                        break;
                }

                if (_characterMainType == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] CharacterMainControl 타입을 찾지 못했습니다.");
                    return;
                }

                BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _characterMainSingletonProp =
                    _characterMainType.GetProperty("Main", flags) ??
                    _characterMainType.GetProperty("Instance", flags) ??
                    _characterMainType.GetProperty("instance", flags);

                if (_characterMainSingletonProp == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] CharacterMainControl 메인 인스턴스 프로퍼티(Main/Instance)를 찾지 못했습니다.");
                }
                else
                {
                    Debug.Log("[AmmoCrosshairHUD] CharacterMainControl 메인 인스턴스 프로퍼티 감지: " + _characterMainSingletonProp.Name);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] CacheCharacterMainReflection 예외: " + ex);
            }
        }

        // ─────────────────────────────────────────────
        // 현재 총 찾기
        // ─────────────────────────────────────────────
        private object GetCurrentGun(object mainObj)
        {
            try
            {
                if (!_holderReflectionCached)
                    CacheHolderReflection(mainObj);

                if (_agentHolderField == null && _agentHolderProp == null)
                    return null;

                object holderObj = GetAgentHolder(mainObj);
                if (holderObj == null)
                    return null;

                if (_currentHoldGunMember == null)
                    CacheCurrentHoldGunMember(holderObj);

                if (_currentHoldGunMember == null)
                    return null;

                return GetMemberValue(holderObj, _currentHoldGunMember);
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] GetCurrentGun 예외: " + ex);
                return null;
            }
        }

        private void CacheHolderReflection(object mainObj)
        {
            try
            {
                Type t = mainObj.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _agentHolderField = t.GetField("agentHolder", flags);
                _agentHolderProp = t.GetProperty("agentHolder", flags) ??
                                   t.GetProperty("AgentHolder", flags);

                if (_agentHolderField == null && _agentHolderProp == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] agentHolder 필드를 CharacterMainControl에서 찾지 못했습니다.");
                }

                _holderReflectionCached = true;
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] CacheHolderReflection 예외: " + ex);
                _holderReflectionCached = true;
            }
        }

        private object GetAgentHolder(object mainObj)
        {
            try
            {
                if (_agentHolderField != null)
                    return _agentHolderField.GetValue(mainObj);

                if (_agentHolderProp != null)
                    return _agentHolderProp.GetValue(mainObj, null);
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] GetAgentHolder 예외: " + ex);
            }

            return null;
        }

        private void CacheCurrentHoldGunMember(object holderObj)
        {
            try
            {
                Type ht = holderObj.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                PropertyInfo p = ht.GetProperty("CurrentHoldGun", flags) ??
                                 ht.GetProperty("currentHoldGun", flags);
                FieldInfo f = ht.GetField("CurrentHoldGun", flags) ??
                              ht.GetField("currentHoldGun", flags);

                _currentHoldGunMember = (MemberInfo)p ?? (MemberInfo)f;

                if (_currentHoldGunMember == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] CurrentHoldGun 멤버를 agentHolder에서 찾지 못했습니다.");
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] CacheCurrentHoldGunMember 예외: " + ex);
            }
        }

        private object GetMemberValue(object target, MemberInfo member)
        {
            try
            {
                FieldInfo fi = member as FieldInfo;
                if (fi != null)
                    return fi.GetValue(target);

                PropertyInfo pi = member as PropertyInfo;
                if (pi != null && pi.CanRead)
                    return pi.GetValue(target, null);
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] GetMemberValue 예외(" + member.Name + "): " + ex);
            }

            return null;
        }

        private void SetMemberValue(object target, MemberInfo member, object value)
        {
            try
            {
                FieldInfo fi = member as FieldInfo;
                if (fi != null)
                {
                    fi.SetValue(target, value);
                    return;
                }

                PropertyInfo pi = member as PropertyInfo;
                if (pi != null && pi.CanWrite)
                {
                    pi.SetValue(target, value, null);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] SetMemberValue 예외(" + member.Name + "): " + ex);
            }
        }

        // ─────────────────────────────────────────────
        // 총/탄약 리플렉션 캐시
        // ─────────────────────────────────────────────
        private void CacheAmmoReflection(object gunObj)
        {
            try
            {
                Type gunType = gunObj.GetType();
                _gunItemProp = gunType.GetProperty("Item");
                if (_gunItemProp == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] gun.Item 프로퍼티를 찾지 못했습니다.");
                    return;
                }

                object item = _gunItemProp.GetValue(gunObj, null);
                if (item == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] gun.Item 값이 null 입니다.");
                    return;
                }

                Type itemType = item.GetType();
                MethodInfo getComponentRaw = itemType.GetMethod("GetComponent", Type.EmptyTypes);
                if (getComponentRaw == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] Item.GetComponent 메서드를 찾지 못했습니다.");
                    return;
                }

                _itemSettingGunType = FindTypeByName("ItemSetting_Gun");
                if (_itemSettingGunType == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] ItemSetting_Gun 타입을 찾지 못했습니다.");
                    return;
                }

                _getComponentMethod = getComponentRaw.MakeGenericMethod(_itemSettingGunType);
                _bulletCountField = _itemSettingGunType.GetField(
                    "_bulletCountCache",
                    BindingFlags.Instance | BindingFlags.NonPublic
                );
                _targetBulletIdProp = _itemSettingGunType.GetProperty("TargetBulletID");

                if (_bulletCountField == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] _bulletCountCache 필드를 찾지 못했습니다.");
                    return;
                }

                _getCurrentLoadedBulletMethod = _itemSettingGunType.GetMethod(
                    "GetCurrentLoadedBullet",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null
                );

                _setTargetBulletTypeMethod = _itemSettingGunType.GetMethod(
                    "SetTargetBulletType",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new Type[] { typeof(Item) }, null
                );

                PropertyInfo prefProp = _itemSettingGunType.GetProperty(
                    "PreferdBulletsToLoad",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                FieldInfo prefField = _itemSettingGunType.GetField(
                    "PreferdBulletsToLoad",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                _preferBulletsMember = (MemberInfo)prefProp ?? (MemberInfo)prefField;

                _ammoReflectionCached = true;
                Debug.Log("[AmmoCrosshairHUD] Ammo reflection 캐시 완료.");
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] CacheAmmoReflection 예외: " + ex);
            }
        }

        private Type FindTypeByName(string simpleName)
        {
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly a = assemblies[i];
                    Type[] types;
                    try { types = a.GetTypes(); }
                    catch { types = new Type[0]; }

                    for (int j = 0; j < types.Length; j++)
                    {
                        Type t = types[j];
                        if (t != null && t.Name == simpleName)
                            return t;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] FindTypeByName 예외: " + ex);
            }

            return null;
        }

        // ─────────────────────────────────────────────
        // 인벤토리 리플렉션
        // ─────────────────────────────────────────────
        private void CacheInventoryReflection(object mainObj)
        {
            try
            {
                Type mainType = mainObj.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                PropertyInfo pChar = mainType.GetProperty("CharacterItem", flags);
                FieldInfo fChar = mainType.GetField("CharacterItem", flags) ??
                                  mainType.GetField("characterItem", flags);
                _characterItemMember = (MemberInfo)pChar ?? (MemberInfo)fChar;

                if (_characterItemMember == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] CharacterItem 멤버를 찾지 못했습니다.");
                    _inventoryReflectionCached = true;
                    return;
                }

                object charItem = GetMemberValue(mainObj, _characterItemMember);
                if (charItem == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] CharacterItem 값이 null 입니다.");
                    _inventoryReflectionCached = true;
                    return;
                }

                Type charItemType = charItem.GetType();
                PropertyInfo pInv = charItemType.GetProperty("Inventory", flags);
                FieldInfo fInv = charItemType.GetField("Inventory", flags) ??
                                 charItemType.GetField("inventory", flags);
                _inventoryMember = (MemberInfo)pInv ?? (MemberInfo)fInv;

                if (_inventoryMember == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] CharacterItem.Inventory 멤버를 찾지 못했습니다.");
                    _inventoryReflectionCached = true;
                    return;
                }

                object inventory = GetMemberValue(charItem, _inventoryMember);
                if (inventory == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] Inventory 객체가 null 입니다.");
                    _inventoryReflectionCached = true;
                    return;
                }

                Type invType = inventory.GetType();
                BindingFlags invFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                PropertyInfo pContent = invType.GetProperty("Content", invFlags) ??
                                        invType.GetProperty("content", invFlags) ??
                                        invType.GetProperty("Items", invFlags) ??
                                        invType.GetProperty("items", invFlags) ??
                                        invType.GetProperty("AllItems", invFlags);
                FieldInfo fContent = invType.GetField("Content", invFlags) ??
                                     invType.GetField("content", invFlags) ??
                                     invType.GetField("Items", invFlags) ??
                                     invType.GetField("items", invFlags) ??
                                     invType.GetField("AllItems", invFlags);

                _inventoryContentMember = (MemberInfo)pContent ?? (MemberInfo)fContent;

                if (_inventoryContentMember == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] Inventory content 멤버를 찾지 못했습니다.");
                }
                else
                {
                    Debug.Log("[AmmoCrosshairHUD] Inventory content 멤버 감지: " + _inventoryContentMember.Name);
                }

                _inventoryReflectionCached = true;
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] CacheInventoryReflection 예외: " + ex);
                _inventoryReflectionCached = true;
            }
        }

        private IEnumerable GetInventoryEnumerable(object mainObj)
        {
            if (!_inventoryReflectionCached)
                CacheInventoryReflection(mainObj);

            if (_inventoryContentMember == null)
                return null;

            try
            {
                object charItem = null;
                if (_characterItemMember != null)
                    charItem = GetMemberValue(mainObj, _characterItemMember);
                if (charItem == null)
                    return null;

                object inventory = null;
                if (_inventoryMember != null)
                    inventory = GetMemberValue(charItem, _inventoryMember);
                if (inventory == null)
                    return null;

                object content = GetMemberValue(inventory, _inventoryContentMember);
                return content as IEnumerable;
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] GetInventoryEnumerable 예외: " + ex);
                return null;
            }
        }

        // ─────────────────────────────────────────────
        // 탄약 값 스캔
        // ─────────────────────────────────────────────
        private void SafeUpdateAmmo()
        {
            object mainObj = GetMainCharacter();
            if (mainObj == null)
                return;

            try
            {
                object gunObj = GetCurrentGun(mainObj);
                if (gunObj == null)
                {
                    _lastAmmoUpdateTime = 0f;
                    _isReloading = false;
                    return;
                }

                if (!_ammoReflectionCached)
                    CacheAmmoReflection(gunObj);

                if (!_ammoReflectionCached)
                    return;

                int mag;
                int reserve;
                if (!ReadAmmoValues(mainObj, gunObj, out mag, out reserve))
                    return;

                _lastMagAmmo = mag;
                _lastReserveAmmo = reserve;
                _lastAmmoUpdateTime = Time.time;

                int gunTypeId = GetGunItemTypeId(gunObj);
                if (gunTypeId != 0)
                {
                    int prev;
                    if (!_clipSizeByGunType.TryGetValue(gunTypeId, out prev) || mag > prev)
                        _clipSizeByGunType[gunTypeId] = mag;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] SafeUpdateAmmo 예외: " + ex);
            }
        }

        private bool ReadAmmoValues(object mainObj, object gunObj, out int mag, out int reserve)
        {
            mag = 0;
            reserve = 0;

            try
            {
                object itemObj = _gunItemProp.GetValue(gunObj, null);
                if (itemObj == null)
                    return false;

                object setting = _getComponentMethod.Invoke(itemObj, null);
                if (setting == null)
                    return false;

                object clipVal = _bulletCountField.GetValue(setting);
                if (clipVal != null)
                    mag = Convert.ToInt32(clipVal);
                if (mag < 0) mag = 0;

                int bulletTypeId = -1;
                if (_targetBulletIdProp != null)
                {
                    object idVal = _targetBulletIdProp.GetValue(setting, null);
                    if (idVal != null)
                        bulletTypeId = Convert.ToInt32(idVal);
                }

                if (bulletTypeId <= 0)
                {
                    // 탄종 ID를 못 읽어도 장전탄은 표시 가능
                    return true;
                }

                IEnumerable contentEnum = GetInventoryEnumerable(mainObj);
                if (contentEnum == null)
                    return true;

                int total = 0;

                foreach (object slot in contentEnum)
                {
                    if (slot == null)
                        continue;

                    Item it = slot as Item;
                    if (it == null)
                        continue;

                    if (it.TypeID != bulletTypeId)
                        continue;

                    int stack = it.StackCount;
                    if (stack > 0)
                        total += stack;
                }

                if (total < 0) total = 0;
                reserve = total;

                return true;
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] ReadAmmoValues 예외: " + ex);
                return false;
            }
        }

        // ─────────────────────────────────────────────
        // 탄종 변경 (우클릭 조준 중 Q)
        // ─────────────────────────────────────────────
        private void TryCycleBulletType(object mainObj, object gunObj)
        {
            try
            {
                if (!_ammoReflectionCached)
                    CacheAmmoReflection(gunObj);

                if (!_ammoReflectionCached)
                    return;

                object itemObj = _gunItemProp.GetValue(gunObj, null);
                Item gunItem = itemObj as Item;
                if (gunItem == null)
                    return;

                object setting = _getComponentMethod.Invoke(itemObj, null);
                if (setting == null)
                    return;

                IEnumerable contentEnum = GetInventoryEnumerable(mainObj);
                if (contentEnum == null)
                    return;

                List<Item> bullets = BuildBulletList(contentEnum, gunItem);
                if (bullets.Count == 0)
                {
                    Debug.Log("[AmmoCrosshairHUD] 탄종 변경: 호환 탄약이 인벤토리에 없습니다.");
                    return;
                }

                int currentTypeId = -1;
                if (_targetBulletIdProp != null)
                {
                    object tid = _targetBulletIdProp.GetValue(setting, null);
                    if (tid != null)
                        currentTypeId = Convert.ToInt32(tid);
                }

                if (currentTypeId <= 0 && _getCurrentLoadedBulletMethod != null)
                {
                    object cb = _getCurrentLoadedBulletMethod.Invoke(setting, null);
                    Item cbItem = cb as Item;
                    if (cbItem != null)
                        currentTypeId = cbItem.TypeID;
                }

                bullets.Sort((a, b) => a.TypeID.CompareTo(b.TypeID));

                int index = 0;
                for (int i = 0; i < bullets.Count; i++)
                {
                    if (bullets[i].TypeID == currentTypeId)
                    {
                        index = (i + 1) % bullets.Count;
                        break;
                    }
                }

                Item nextBullet = bullets[index];

                if (_setTargetBulletTypeMethod != null)
                {
                    _setTargetBulletTypeMethod.Invoke(setting, new object[] { nextBullet });
                }

                if (_preferBulletsMember != null)
                {
                    SetMemberValue(setting, _preferBulletsMember, nextBullet);
                }

                Debug.Log("[AmmoCrosshairHUD] 탄종 변경: " + nextBullet.DisplayName + " (TypeID=" + nextBullet.TypeID + ")");
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] TryCycleBulletType 예외: " + ex);
            }
        }

        private List<Item> BuildBulletList(IEnumerable contentEnum, Item gunItem)
        {
            List<Item> result = new List<Item>();

            try
            {
                if (gunItem == null || contentEnum == null)
                    return result;

                // 총의 탄종(Caliber) 읽기
                string caliber = null;
                try
                {
                    var constants = gunItem.Constants;
                    if (constants != null)
                    {
                        caliber = constants.GetString("Caliber", null);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log("[AmmoCrosshairHUD] BuildBulletList - Caliber 읽기 예외: " + ex);
                }

                if (string.IsNullOrEmpty(caliber))
                    return result;

                HashSet<int> seenTypeIds = new HashSet<int>();

                foreach (object obj in contentEnum)
                {
                    Item it = obj as Item;
                    if (it == null)
                        continue;

                    bool isBullet = it.GetBool("IsBullet", false);
                    if (!isBullet)
                        continue;

                    string bCaliber = null;
                    try
                    {
                        var bConsts = it.Constants;
                        if (bConsts != null)
                            bCaliber = bConsts.GetString("Caliber", null);
                    }
                    catch { }

                    if (string.IsNullOrEmpty(bCaliber) || bCaliber != caliber)
                        continue;

                    if (it.StackCount <= 0)
                        continue;

                    if (seenTypeIds.Contains(it.TypeID))
                        continue;

                    seenTypeIds.Add(it.TypeID);
                    result.Add(it);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] BuildBulletList 예외: " + ex);
            }

            return result;
        }
    }
}
