using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using Duckov;

namespace ammocrosshairhud
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                GameObject root = new GameObject("AmmoCrosshairHUDRoot");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.AddComponent<AmmoCrosshairHUDManager>();
                Debug.Log("[AmmoCrosshairHUD] OnAfterSetup - HUD 초기화 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] OnAfterSetup 예외: " + ex);
            }
        }
    }

    public class AmmoCrosshairHUDManager : MonoBehaviour
    {
        private static AmmoCrosshairHUDManager _instance;

        // CharacterMainControl → agentHolder → CurrentHoldGun
        private bool _holderReflectionCached;
        private FieldInfo _agentHolderField;
        private PropertyInfo _agentHolderProp;
        private MemberInfo _currentHoldGunMember;

        // 총 내부 (Item / ItemSetting_Gun)
        private bool _ammoReflectionCached;
        private PropertyInfo _gunItemProp;        // gun.Item
        private MethodInfo _getComponentMethod;   // item.GetComponent<ItemSetting_Gun>()
        private Type _itemSettingGunType;         // ItemSetting_Gun
        private FieldInfo _bulletCountField;      // _bulletCountCache
        private PropertyInfo _targetBulletIdProp; // TargetBulletID

        // 플레이어 인벤토리: CharacterMainControl.CharacterItem.Inventory.Content / Items / AllItems
        private bool _inventoryReflectionCached;
        private MemberInfo _characterItemMember;     // CharacterMainControl.CharacterItem / characterItem
        private MemberInfo _inventoryMember;         // CharacterItem.Inventory / inventory
        private MemberInfo _inventoryContentMember;  // Inventory.Content / Items / AllItems

        // HUD 값
        private int _lastMagAmmo;
        private int _lastReserveAmmo;
        private float _lastAmmoUpdateTime;

        // GUI
        private GUIStyle _ammoStyle;

        // 업데이트 간격
        private float _nextScanTime;

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
            if (Time.time < _nextScanTime)
                return;

            _nextScanTime = Time.time + 0.1f;
            TryUpdateAmmo();
        }

        private void TryUpdateAmmo()
        {
            try
            {
                CharacterMainControl main = CharacterMainControl.Main;
                if (main == null)
                {
                    _lastAmmoUpdateTime = 0f;
                    return;
                }

                // 1) agentHolder 경로 캐시
                if (!_holderReflectionCached)
                    CacheHolderReflection(main);

                if (_agentHolderField == null && _agentHolderProp == null)
                {
                    _lastAmmoUpdateTime = 0f;
                    return;
                }

                // 2) agentHolder 인스턴스
                object holderObj = GetAgentHolder(main);
                if (holderObj == null)
                {
                    _lastAmmoUpdateTime = 0f;
                    return;
                }

                // 3) CurrentHoldGun 멤버 캐시
                if (_currentHoldGunMember == null)
                    CacheCurrentHoldGunMember(holderObj);

                if (_currentHoldGunMember == null)
                {
                    _lastAmmoUpdateTime = 0f;
                    return;
                }

                // 4) 현재 들고 있는 총 객체
                object gunObj = GetMemberValue(holderObj, _currentHoldGunMember);
                if (gunObj == null)
                {
                    _lastAmmoUpdateTime = 0f;
                    return;
                }

                // 5) 총/탄약 구조 리플렉션 경로 캐시
                if (!_ammoReflectionCached)
                    CacheAmmoReflection(gunObj);

                if (!_ammoReflectionCached)
                {
                    _lastAmmoUpdateTime = 0f;
                    return;
                }

                // 6) 장전 / 인벤 탄수 읽기
                if (!ReadAmmoValues(main, gunObj, out int mag, out int reserve))
                {
                    _lastAmmoUpdateTime = 0f;
                    return;
                }

                _lastMagAmmo = mag;
                _lastReserveAmmo = reserve;
                _lastAmmoUpdateTime = Time.time;
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] TryUpdateAmmo 예외: " + ex);
                _lastAmmoUpdateTime = 0f;
            }
        }

        // ───────── CharacterMainControl → agentHolder ─────────

        private void CacheHolderReflection(CharacterMainControl main)
        {
            try
            {
                Type t = main.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _agentHolderField = t.GetField("agentHolder", flags);
                _agentHolderProp = t.GetProperty("agentHolder", flags) ??
                                    t.GetProperty("AgentHolder", flags);

                if (_agentHolderField == null && _agentHolderProp == null)
                    Debug.Log("[AmmoCrosshairHUD] agentHolder 필드를 CharacterMainControl에서 찾지 못했습니다.");

                _holderReflectionCached = true;
            }
            catch (Exception ex)
            {
                Debug.Log("[AmmoCrosshairHUD] CacheHolderReflection 예외: " + ex);
                _holderReflectionCached = true;
            }
        }

        private object GetAgentHolder(CharacterMainControl main)
        {
            try
            {
                if (_agentHolderField != null)
                    return _agentHolderField.GetValue(main);

                if (_agentHolderProp != null)
                    return _agentHolderProp.GetValue(main, null);
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

                _currentHoldGunMember = (MemberInfo)p ?? f;

                if (_currentHoldGunMember == null)
                    Debug.Log("[AmmoCrosshairHUD] CurrentHoldGun 멤버를 agentHolder에서 찾지 못했습니다.");
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

        // ───────── 총 내부 리플렉션 캐시 ─────────

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
                _bulletCountField = _itemSettingGunType.GetField("_bulletCountCache",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                _targetBulletIdProp = _itemSettingGunType.GetProperty("TargetBulletID");

                if (_bulletCountField == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] _bulletCountCache 필드를 찾지 못했습니다.");
                    return;
                }

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
                    catch { types = Array.Empty<Type>(); }

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

        // ───────── CharacterItem.Inventory.content 리플렉션 ─────────

        private void CacheInventoryReflection(CharacterMainControl main)
        {
            try
            {
                Type mainType = main.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // CharacterItem
                PropertyInfo pChar = mainType.GetProperty("CharacterItem", flags);
                FieldInfo fChar = mainType.GetField("CharacterItem", flags) ??
                                     mainType.GetField("characterItem", flags);
                _characterItemMember = (MemberInfo)pChar ?? fChar;

                if (_characterItemMember == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] CharacterItem 멤버를 찾지 못했습니다.");
                    _inventoryReflectionCached = true;
                    return;
                }

                object charItem = GetMemberValue(main, _characterItemMember);
                if (charItem == null)
                {
                    Debug.Log("[AmmoCrosshairHUD] CharacterItem 값이 null 입니다.");
                    _inventoryReflectionCached = true;
                    return;
                }

                // CharacterItem.Inventory
                Type charItemType = charItem.GetType();
                PropertyInfo pInv = charItemType.GetProperty("Inventory", flags);
                FieldInfo fInv = charItemType.GetField("Inventory", flags) ??
                                    charItemType.GetField("inventory", flags);
                _inventoryMember = (MemberInfo)pInv ?? fInv;

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

                // Inventory.Content / Items / AllItems ...
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

                _inventoryContentMember = (MemberInfo)pContent ?? fContent;

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

        private IEnumerable GetInventoryEnumerable(CharacterMainControl main)
        {
            if (!_inventoryReflectionCached)
                CacheInventoryReflection(main);

            if (_inventoryContentMember == null)
                return null;

            try
            {
                object charItem = null;
                if (_characterItemMember != null)
                    charItem = GetMemberValue(main, _characterItemMember);
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

        // ───────── 장전 / 인벤 탄 읽기 ─────────
        //   왼쪽: _bulletCountCache (장전수)
        //   오른쪽: CharacterItem.Inventory 안에서
        //           TypeID == TargetBulletID 인 슬롯들의 StackCount 합계
        private bool ReadAmmoValues(CharacterMainControl main, object gunObj, out int mag, out int reserve)
        {
            mag = 0;
            reserve = 0;

            try
            {
                object item = _gunItemProp.GetValue(gunObj, null);
                if (item == null)
                    return false;

                object setting = _getComponentMethod.Invoke(item, null);
                if (setting == null)
                    return false;

                // 장전 탄
                object clipVal = _bulletCountField.GetValue(setting);
                if (clipVal != null)
                    mag = Convert.ToInt32(clipVal);
                if (mag < 0) mag = 0;

                // 탄종 ID
                int bulletTypeId = -1;
                if (_targetBulletIdProp != null)
                {
                    object idVal = _targetBulletIdProp.GetValue(setting, null);
                    if (idVal != null)
                        bulletTypeId = Convert.ToInt32(idVal);
                }

                if (bulletTypeId <= 0)
                {
                    // 탄종 ID를 못 찾으면 장전 탄만이라도 표시
                    return true;
                }

                IEnumerable contentEnum = GetInventoryEnumerable(main);
                if (contentEnum == null)
                    return true; // 인벤토리 없으면 장전 탄만

                int total = 0;

                foreach (object slot in contentEnum)
                {
                    if (slot == null)
                        continue;

                    Type st = slot.GetType();
                    BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                    PropertyInfo typeIdProp = st.GetProperty("TypeID", flags) ??
                                              st.GetProperty("typeID", flags);
                    PropertyInfo stackProp = st.GetProperty("StackCount", flags) ??
                                              st.GetProperty("stackCount", flags);

                    if (stackProp == null)
                        continue;

                    int typeId = -1;
                    if (typeIdProp != null)
                    {
                        object typeIdVal = typeIdProp.GetValue(slot, null);
                        if (typeIdVal != null)
                            typeId = Convert.ToInt32(typeIdVal);
                    }

                    if (typeId != bulletTypeId)
                        continue;

                    object stackVal = stackProp.GetValue(slot, null);
                    if (stackVal == null)
                        continue;

                    int stack = Convert.ToInt32(stackVal);
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

        // ───────── OnGUI ─────────

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint)
                return;

            if (_lastAmmoUpdateTime <= 0f)
                return;

            float age = Time.time - _lastAmmoUpdateTime;
            if (age > 5f)
                return;

            if (_ammoStyle == null)
                SetupStyle();

            string text = _lastMagAmmo.ToString() + " / " + _lastReserveAmmo.ToString();

            float centerX = Screen.width * 0.5f;
            float centerY = Screen.height * 0.5f;

            // 크로스헤어 기준 살짝 오른쪽/아래
            float offsetX = 40f;
            float offsetY = 10f;
            float width = 140f;
            float height = 24f;

            Rect rect = new Rect(centerX + offsetX,
                                 centerY + offsetY - height * 0.5f,
                                 width,
                                 height);

            DrawLabelWithOutline(rect, text, _ammoStyle, Color.black, Color.white);
        }

        private void SetupStyle()
        {
            _ammoStyle = new GUIStyle(GUI.skin.label);
            _ammoStyle.fontSize = 18;
            // TextAnchor / FontStyle 안 씀 (TextRenderingModule 참조 불필요)
            _ammoStyle.padding = new RectOffset(4, 4, 0, 0);
        }

        private void DrawLabelWithOutline(Rect rect, string text, GUIStyle style, Color outline, Color inner)
        {
            Color oldColor = GUI.color;

            GUI.color = outline;

            Rect r = rect;
            r.x -= 1f;
            GUI.Label(r, text, style);
            r.x += 2f;
            GUI.Label(r, text, style);
            r.x -= 1f;
            r.y -= 1f;
            GUI.Label(r, text, style);
            r.y += 2f;
            GUI.Label(r, text, style);

            GUI.color = inner;
            GUI.Label(rect, text, style);

            GUI.color = oldColor;
        }
    }
}
