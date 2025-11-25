using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;

namespace ammocrosshairhud
{
    // Duckov 모드 로더가 찾는 엔트리 타입
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
                Debug.Log("[AmmoCrosshairHUD] ModBehaviour.OnAfterSetup 예외: " + ex);
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

        // 크로스헤어 기준 패널 위치
        private float _panelOffsetX = 160f; // 너가 찾은 최적값
        private float _panelOffsetY = 0f;

        // ─────────────────────────────────────────────
        // 리로드 상태 (gunState == 5 일 때만 떨림)
        // ─────────────────────────────────────────────
        private bool _reloadStateReflectionCached;
        private FieldInfo _gunStateField;
        private const int RELOAD_STATE_VALUE = 5; // gunState == 5 → 리로드 중
        private bool _isReloading;

        // 총 타입별 탄창 크기(최대 장전 수) - 필요하면 쓰고, 안 써도 문제 없음
        private readonly Dictionary<int, int> _clipSizeByGunType = new Dictionary<int, int>();
        private int _currentGunTypeId;

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
            // 1) 리로드 여부만 추적 (진행도 X)
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

            // 6) HUD 갱신
            if (_uiReady && _ammoPanelRect != null && _ammoText != null)
            {
                UpdateAmmoUI();
            }
        }

        // ─────────────────────────────────────────────
        // AimMarker / DistanceIndicator 기반 UI 세팅
        // ─────────────────────────────────────────────
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

                // 기존 거리 텍스트: AimMarker/DistanceIndicator/Background/Text
                Transform distanceTextTr = aimTr.Find("DistanceIndicator/Background/Text");
                if (distanceTextTr == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] DistanceIndicator/Background/Text 경로를 찾지 못했습니다.");
                    return;
                }

                // 거리 텍스트의 Rect / 폰트만 참고용으로 사용
                RectTransform backgroundTemplate = distanceTextTr.parent as RectTransform;
                TMPro.TextMeshProUGUI templateText = distanceTextTr.GetComponent<TMPro.TextMeshProUGUI>();

                if (backgroundTemplate == null || templateText == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] 거리 텍스트 템플릿 정보를 읽지 못했습니다.");
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

                // ───── 여기부터: "배경 템플릿 복제" 대신 "새 패널 + 새 Text" 생성 ─────

                // 패널 GameObject 새로 만들기 (아이콘/이미지 없음)
                GameObject panelObj = new GameObject("AmmoHUDPanel (Modded)");
                panelObj.transform.SetParent(_aimMarkerUIRoot, false);

                _ammoPanelRect = panelObj.AddComponent<RectTransform>();
                _ammoPanelRect.anchorMin = new Vector2(0.5f, 0.5f);
                _ammoPanelRect.anchorMax = new Vector2(0.5f, 0.5f);
                _ammoPanelRect.pivot = new Vector2(1f, 0.5f);

                // 거리 텍스트 배경 크기만 참고해서 사이즈 맞추기
                _ammoPanelRect.sizeDelta = backgroundTemplate.sizeDelta;

                // 자식으로 Text 오브젝트 생성
                GameObject textObj = new GameObject("Text");
                textObj.transform.SetParent(_ammoPanelRect, false);
                _ammoText = textObj.AddComponent<TMPro.TextMeshProUGUI>();

                // 폰트/색/머티리얼은 거리 텍스트 템플릿에서 그대로 복사
                _ammoText.font = templateText.font;
                _ammoText.fontSize = templateText.fontSize;
                _ammoText.color = templateText.color;
                _ammoText.fontMaterial = templateText.fontMaterial;

                // 나머지 옵션
                _ammoText.text = string.Empty;
                _ammoText.enableWordWrapping = false;
                _ammoText.alignment = TMPro.TextAlignmentOptions.Left;

                // 처음엔 숨겨두기
                _ammoPanelRect.gameObject.SetActive(false);

                _uiReady = true;
                Debug.Log("[AmmoCrosshairHUD] AimMarker 기반 탄약 UI 설정 완료(아이콘 없이 폰트만 복사).");
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] TrySetupUI 예외: " + ex);
            }
        }


        // ─────────────────────────────────────────────
        // 리로드 여부만 추적 (진행도 X, 떨림용)
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
        // HUD 숫자 갱신 (그냥 실제 값 + 리로드 중 떨림만)
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

                _ammoText.text = showMag.ToString() + " / " + showReserve.ToString();

                if (_aimRight != null && _ammoPanelRect != null)
                {
                    Vector2 basePos = _aimRight.anchoredPosition;
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
