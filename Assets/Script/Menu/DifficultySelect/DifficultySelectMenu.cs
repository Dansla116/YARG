using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.AddressableAssets;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Song;
using YARG.Core.Utility;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Player;
using YARG.Song;

namespace YARG.Menu.DifficultySelect
{
    public class DifficultySelectMenu : MonoBehaviour
    {
        /// <summary>
        /// The saved song speed value
        /// </summary>
        private static float _songSpeed = 1f;

        private enum State
        {
            Main,
            Instrument,
            Difficulty,
            Modifiers,
            Harmony
        }

        [SerializeField]
        private TextMeshProUGUI _subHeader;
        [SerializeField]
        private Transform _container;
        [SerializeField]
        private NavigationGroup _navGroup;
        [SerializeField]
        private TextMeshProUGUI _text;
        [SerializeField]
        private TMP_InputField _speedInput;
        [SerializeField]
        private TextMeshProUGUI _loadingPhrase;
        [SerializeField]
        private TextMeshProUGUI _warningMessage;
        [SerializeField]
        private GameObject _warningMessageContainer;

        [Space]
        [SerializeField]
        private TextMeshProUGUI _songTitleText;
        [SerializeField]
        private TextMeshProUGUI _artistText;
        [SerializeField]
        private Image _sourceIcon;

        [Space]
        [SerializeField]
        private DifficultyItem _difficultyItemPrefab;
        [SerializeField]
        private DifficultyItem _difficultyGreenPrefab;
        [SerializeField]
        private DifficultyItem _difficultyItemSmallRedPrefab;
        [SerializeField]
        private ModifierItem _modifierItemPrefab;

        private sealed class PlayerMenuPanel
        {
            public YargPlayer Player;
            public List<YargPlayer> VocalPlayers;
            public RectTransform Root;
            public Transform Container;
            public NavigationGroup NavGroup;
            public TextMeshProUGUI HeaderText;
            public Image HeaderIcon;
            public Scrollbar Scrollbar;

            public State MenuState;
            public State LastMenuState;

            public int MaxHarmonyIndex;
            public Modifier ExcusableModifiers;

            public bool IsReady;
            public bool IsVocalGroup;

            public readonly List<Instrument> PossibleInstruments = new();
            public readonly List<Difficulty> PossibleDifficulties = new();
            public readonly List<Modifier> PossibleModifiers = new();
            public readonly List<ModifierItem> ModifierItems = new();

            public NavigationGroup.SelectionAction SelectionHandler;
        }

        private readonly List<PlayerMenuPanel> _panels = new();
        private readonly Dictionary<YargPlayer, PlayerMenuPanel> _panelByPlayer = new();
        private readonly HashSet<YargPlayer> _readyPlayers = new();

        private RectTransform _menuTemplate;
        private RectTransform _menusRoot;

        private YargPlayer _vocalModifierOwner;
        private YargPlayer _lastActivePlayer;
        private int _globalMaxHarmonyIndex = 3;
        private float _headerBaseFontSize;
        private static readonly Dictionary<string, Sprite> _headerIconCache = new();
        private const float HeaderIconSize = 40f;
        private static readonly Vector2 HeaderIconAnchoredPosition = new(40f, 0f);

        private List<SongEntry> _songList;

        private void OnEnable()
        {
            string subHeaderKey = GlobalVariables.State.IsPractice ? "Practice" : "Quickplay";
            _subHeader.text = Localize.Key("Menu.Main.Options", subHeaderKey);

            // Set navigation scheme
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Up, "Menu.Common.Up", HandleNavigation),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down", HandleNavigation),
                new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Confirm", HandleNavigation),
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", HandleBack)
            }, false));

            _speedInput.text = $"{Mathf.RoundToInt(_songSpeed * 100f)}%";
            _songTitleText.text = GlobalVariables.State.CurrentSong.Name;
            _artistText.text = GlobalVariables.State.CurrentSong.Artist;

            if (GlobalVariables.State.PlayingAShow)
            {
                _songList = GlobalVariables.State.ShowSongs;
            }
            else
            {
                _songList = new List<SongEntry> { GlobalVariables.State.CurrentSong };
            }

            _loadingPhrase.text = RichTextUtils.StripRichTextTags(
                GlobalVariables.State.CurrentSong.LoadingPhrase, RichTextTags.BadTags);

            _sourceIcon.sprite = SongSources.SourceToIcon(GlobalVariables.State.CurrentSong.Source);
            _sourceIcon.gameObject.SetActive(_sourceIcon.sprite != null);

            SetupMenuPanels();
            RebuildAllPlayers();
            LayoutPanels();

            _lastActivePlayer = PlayerContainer.Players.FirstOrDefault();
            UpdateWarningForPlayer(_lastActivePlayer);
        }
        private void HandleNavigation(NavigationContext context)
        {
            if (!TryGetPanel(context.Player, out var panel)) return;

            _lastActivePlayer = context.Player;
            UpdateWarningForPlayer(context.Player);

            switch (context.Action)
            {
                case MenuAction.Up:
                    panel.NavGroup?.SelectPrevious(context.IsRepeat);
                    break;
                case MenuAction.Down:
                    panel.NavGroup?.SelectNext(context.IsRepeat);
                    break;
                case MenuAction.Green:
                    panel.NavGroup?.ConfirmSelection();
                    break;
            }
        }

        private void HandleBack(NavigationContext context)
        {
            if (!TryGetPanel(context.Player, out var panel)) return;

            _lastActivePlayer = context.Player;
            UpdateWarningForPlayer(context.Player);

            if (panel.MenuState == State.Main)
            {
                if (PlayerContainer.Players.Count > 0 &&
                    PlayerContainer.Players[0] == panel.Player)
                {
                    MenuManager.Instance.PopMenu();
                }
                return;
            }

            panel.MenuState = State.Main;
            UpdateForPlayer(panel);
        }

        private void ShowWarning(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                _warningMessageContainer.SetActive(false);
                _warningMessage.text = "";
            }
            else
            {
                _warningMessageContainer.SetActive(true);
                _warningMessage.text = message;
            }
        }

        private void UpdateForSelectionChanged(PlayerMenuPanel panel, NavigatableBehaviour navigatableBehaviour,
            SelectionOrigin selectionOrigin)
        {
            if (panel.Scrollbar == null) return;

            int? index = panel.NavGroup.SelectedIndex;
            if (index is { } i)
            {
                int count = panel.NavGroup.Count;
                float highScrollBound = panel.Scrollbar.size + (1 - panel.Scrollbar.size) * panel.Scrollbar.value;
                float lowScrollBound = (1 - panel.Scrollbar.size) * panel.Scrollbar.value;
                float indexHighBound = 1 - (1 / (float) count) * i;
                float indexLowBound = 1 - (1 / (float) count) * (i + 1);
                if (highScrollBound < indexHighBound)
                {
                    panel.Scrollbar.value = (indexHighBound - panel.Scrollbar.size) / (1 - panel.Scrollbar.size);
                }
                else if (lowScrollBound > indexLowBound)
                {
                    panel.Scrollbar.value = indexLowBound / (1 - panel.Scrollbar.size);
                }
            }
        }

        private void UpdateForPlayer(PlayerMenuPanel panel)
        {
            // Set player text
            var profile = panel.Player.Profile;
            if (panel.HeaderText != null)
            {
                UpdateHeaderIcon(panel, profile.GameMode);
                if (panel.IsVocalGroup)
                {
                    string names = panel.VocalPlayers != null
                        ? string.Join(", ", panel.VocalPlayers.Select(player => player.Profile.Name))
                        : Instrument.Vocals.ToLocalizedName();

                    SetHeaderText(panel.HeaderText, names, true);
                }
                else
                {
                    SetHeaderText(panel.HeaderText, profile.Name, false);
                }
            }

            // Reset content
            panel.NavGroup?.ClearNavigatables();
            panel.Container.DestroyChildren();

            // Create the menu
            switch (panel.MenuState)
            {
                case State.Main:
                    CreateMainMenu(panel);
                    break;
                case State.Instrument:
                    CreateInstrumentMenu(panel);
                    break;
                case State.Difficulty:
                    CreateDifficultyMenu(panel);
                    break;
                case State.Modifiers:
                    CreateModifierMenu(panel);
                    break;
                case State.Harmony:
                    CreateHarmonyMenu(panel);
                    break;
            }

            panel.LastMenuState = panel.MenuState;
        }

        private void CreateMainMenu(PlayerMenuPanel panel)
        {
            var player = panel.Player;

            // Only show all these options if there are instruments available
            if (panel.PossibleInstruments.Count > 0)
            {
                // Ready button
                CreateItem(panel, LocalizeHeader("Ready"), panel.LastMenuState == State.Main, _difficultyGreenPrefab, () =>
                {
                    SetReady(panel, !panel.IsReady);
                    TryStartGame();
                });

                CreateItem(panel, LocalizeHeader("Instrument"),
                    player.Profile.CurrentInstrument.ToLocalizedName(),
                    panel.LastMenuState == State.Instrument, () =>
                {
                    panel.MenuState = State.Instrument;
                    UpdateForPlayer(panel);
                });

                CreateItem(panel, LocalizeHeader("Difficulty"),
                    player.Profile.CurrentDifficulty.ToLocalizedName(),
                    panel.LastMenuState == State.Difficulty, () =>
                {
                    panel.MenuState = State.Difficulty;
                    UpdateForPlayer(panel);
                });

                // Harmony players must pick their harmony index unless multiple vocalists are grouped
                bool allowHarmonySelect = player.Profile.CurrentInstrument == Instrument.Harmony
                    && (!panel.IsVocalGroup || panel.VocalPlayers == null || panel.VocalPlayers.Count <= 1);
                if (allowHarmonySelect)
                {
                    CreateItem(panel, LocalizeHeader("Harmony"),
                        (player.Profile.HarmonyIndex + 1).ToString(),
                        panel.LastMenuState == State.Harmony, () =>
                    {
                        panel.MenuState = State.Harmony;
                        UpdateForPlayer(panel);
                    });
                }

                // Only allow vocal modifiers to be selected once (so they don't conflict)
                if (player.Profile.GameMode != GameMode.Vocals ||
                    _vocalModifierOwner == null ||
                    _vocalModifierOwner == player ||
                    (panel.IsVocalGroup && panel.VocalPlayers != null && panel.VocalPlayers.Contains(_vocalModifierOwner)))
                {
                    // Create modifiers body text
                    string modifierText = "";
                    if ((player.Profile.CurrentModifiers & ~panel.ExcusableModifiers) == Modifier.None)
                    {
                        // If there are no modifiers (ignoring the excusable ones), then just say "none"
                        modifierText = Modifier.None.ToLocalizedName();
                    }
                    else
                    {
                        // Combine all modifiers
                        foreach (var modifier in panel.PossibleModifiers)
                        {
                            if (!player.Profile.IsModifierActive(modifier)) continue;

                            modifierText += modifier.ToLocalizedName() + "\n";
                        }

                        modifierText = modifierText.Trim();
                    }

                    CreateItem(panel, LocalizeHeader("Modifiers"),
                        modifierText, panel.LastMenuState == State.Modifiers, () =>
                    {
                        if (player.Profile.GameMode == GameMode.Vocals && _vocalModifierOwner == null)
                            _vocalModifierOwner = player;

                        panel.MenuState = State.Modifiers;
                        UpdateForPlayer(panel);
                    });
                }
            }

            // Only show if there is more than one player, only if there is instruments available
            if (panel.PossibleInstruments.Count <= 0 || PlayerContainer.Players.Count != 1)
            {
                // Sit out button
                CreateItem(panel, LocalizeHeader("SitOut"), panel.PossibleInstruments.Count <= 0, _difficultyItemSmallRedPrefab, () =>
                {
                    if (_vocalModifierOwner == player ||
                        (panel.IsVocalGroup && panel.VocalPlayers != null && panel.VocalPlayers.Contains(_vocalModifierOwner)))
                    {
                        _vocalModifierOwner = null;
                    }

                    SetReady(panel, false);
                    if (panel.IsVocalGroup)
                    {
                        foreach (var vocalPlayer in panel.VocalPlayers)
                        {
                            vocalPlayer.SittingOut = true;
                        }
                    }
                    else
                    {
                        player.SittingOut = true;
                    }
                    RebuildAllPlayers();
                });

                // Disconnect button
                CreateItem(panel, LocalizeHeader("Disconnect"), panel.PossibleInstruments.Count <= 0, _difficultyItemSmallRedPrefab, () =>
                {
                    if (_vocalModifierOwner == player ||
                        (panel.IsVocalGroup && panel.VocalPlayers != null && panel.VocalPlayers.Contains(_vocalModifierOwner)))
                    {
                        _vocalModifierOwner = null;
                    }

                    SetReady(panel, false);
                    if (panel.IsVocalGroup)
                    {
                        foreach (var vocalPlayer in panel.VocalPlayers.ToArray())
                        {
                            PlayerContainer.DisposePlayer(vocalPlayer);
                        }
                    }
                    else
                    {
                        PlayerContainer.DisposePlayer(player);
                    }
                    SetupMenuPanels();
                    RebuildAllPlayers();
                    LayoutPanels();
                });
            }
        }
        private void CreateInstrumentMenu(PlayerMenuPanel panel)
        {
            foreach (var instrument in panel.PossibleInstruments)
            {
                bool selected = panel.Player.Profile.CurrentInstrument == instrument;
                CreateItem(panel, instrument.ToLocalizedName(), selected, () =>
                {
                    _lastActivePlayer = panel.Player;

                    foreach (var target in EnumeratePanelPlayers(panel))
                    {
                        var profile = target.Profile;
                        var preferredInstrument = profile.PreferredInstrument;
                        profile.CurrentInstrument = instrument;

                        // What we are doing here is resetting preferred instrument only if the current preferred instrument
                        // was an option for this chart. This ensures that preferred instrument does not change when the
                        // player is forced to use a different instrument.
                        if (!panel.IsVocalGroup &&
                            instrument != preferredInstrument &&
                            panel.PossibleInstruments.Contains(preferredInstrument))
                        {
                            profile.PreferredInstrument = instrument;
                        }
                    }

                    SetReady(panel, false);
                    panel.MenuState = State.Main;
                    RebuildAllPlayers();
                });
            }
        }

        private void CreateDifficultyMenu(PlayerMenuPanel panel)
        {
            foreach (var difficulty in panel.PossibleDifficulties)
            {
                bool selected = panel.Player.Profile.CurrentDifficulty == difficulty;
                CreateItem(panel, difficulty.ToLocalizedName(), selected, () =>
                {
                    foreach (var target in EnumeratePanelPlayers(panel))
                    {
                        target.Profile.CurrentDifficulty
                            = target.Profile.DifficultyFallback
                            = difficulty;
                    }

                    SetReady(panel, false);
                    panel.MenuState = State.Main;
                    UpdateForPlayer(panel);
                });
            }
        }

        private void CreateModifierMenu(PlayerMenuPanel panel)
        {
            var profile = panel.Player.Profile;

            panel.ModifierItems.Clear();
            foreach (var modifier in panel.PossibleModifiers)
            {
                var btn = Instantiate(_modifierItemPrefab, panel.Container);
                btn.Initialize(modifier.ToLocalizedName(), profile.IsModifierActive(modifier), active =>
                {
                    // Enable/disable the modifier
                    foreach (var target in EnumeratePanelPlayers(panel))
                    {
                        if (active)
                        {
                            target.Profile.AddSingleModifier(modifier);
                        }
                        else
                        {
                            target.Profile.RemoveModifiers(modifier);
                        }
                    }

                    SetReady(panel, false);
                    UpdateModifierMenu(panel);
                });

                panel.NavGroup.AddNavigatable(btn);
                panel.ModifierItems.Add(btn);
            }

            // Create done button
            CreateItem(panel, LocalizeHeader("Done"), _difficultyGreenPrefab, () =>
            {
                panel.MenuState = State.Main;
                UpdateForPlayer(panel);
            });

            panel.NavGroup?.SelectFirst();
        }

        private void CreateHarmonyMenu(PlayerMenuPanel panel)
        {
            for (int i = 0; i < panel.MaxHarmonyIndex; i++)
            {
                int capture = i;
                bool selected = panel.Player.Profile.HarmonyIndex == i;
                CreateItem(panel, (i + 1).ToString(), selected, () =>
                {
                    panel.Player.Profile.HarmonyIndex = (byte) capture;

                    SetReady(panel, false);
                    panel.MenuState = State.Main;
                    UpdateForPlayer(panel);
                });
            }
        }

        private void UpdateModifierMenu(PlayerMenuPanel panel)
        {
            var profile = panel.Player.Profile;

            for (int i = 0; i < panel.ModifierItems.Count; i++)
            {
                var item = panel.ModifierItems[i];
                var modifier = panel.PossibleModifiers[i];

                item.Active = profile.IsModifierActive(modifier);
            }
        }

        private void UpdatePossibleModifiers(PlayerMenuPanel panel)
        {
            var profile = panel.Player.Profile;

            // Get the possible modifiers (split the enum into multiple) and
            // make sure current modifiers are valid, and remove the invalid ones
            panel.PossibleModifiers.Clear();
            var (possible, excusable) = profile.GameMode.PossibleModifiers(profile.CurrentInstrument);
            panel.ExcusableModifiers = excusable;

            foreach (var modifier in EnumExtensions<Modifier>.Values)
            {
                // Skip if the modifier is not a possible one
                if ((possible & modifier) == 0)
                {
                    // Also try to clear it if it isn't considered excusable yet the player somehow has it
                    if (((excusable & modifier) == 0) && profile.IsModifierActive(modifier))
                        profile.RemoveModifiers(modifier);

                    continue;
                }

                panel.PossibleModifiers.Add(modifier);
            }

        }

        private void UpdatePossibleDifficulties(PlayerMenuPanel panel)
        {
            panel.PossibleDifficulties.Clear();

            var profile = panel.Player.Profile;

            // Get the possible difficulties for the player's instrument in the song
            foreach (var difficulty in EnumExtensions<Difficulty>.Values)
            {
                bool invalidDifficulty = false;
                foreach (var showsong in _songList)
                {
                    if (!HasPlayableDifficulty(showsong, profile.CurrentInstrument, difficulty))
                    {
                        invalidDifficulty = true;
                        break;
                    }
                }

                if (!invalidDifficulty)
                    panel.PossibleDifficulties.Add(difficulty);
            }

            // TODO: Handle difficulty fallback better in play a show mode
            var diff = (int) profile.DifficultyFallback;
            while (diff >= (int) Difficulty.Beginner && !panel.PossibleDifficulties.Contains((Difficulty) diff))
            {
                --diff;
            }

            if (diff < (int) Difficulty.Beginner)
            {
                diff = (int) profile.DifficultyFallback;
                while (diff < (int) Difficulty.ExpertPlus)
                {
                    ++diff;
                    if (panel.PossibleDifficulties.Contains((Difficulty) diff)) break;
                }
            }
            profile.CurrentDifficulty = (Difficulty) diff;
        }

        private void OnDisable()
        {
            Navigator.Instance.PopScheme();
            ClearPanels();
        }

        private void CreateItem(PlayerMenuPanel panel, string header, string body, bool selected, DifficultyItem difficultyItem, UnityAction a)
        {
            var btn = Instantiate(difficultyItem, panel.Container);

            if (header is null)
            {
                btn.Initialize(body, a);
            }
            else
            {
                btn.Initialize(header, body, a);
            }

            panel.NavGroup.AddNavigatable(btn.Button);

            if (selected)
                panel.NavGroup?.SelectLast();
        }

        private void CreateItem(PlayerMenuPanel panel, string body, bool selected, DifficultyItem difficultyItem, UnityAction a)
        {
            CreateItem(panel, null, body, selected, difficultyItem, a);
        }

        private void CreateItem(PlayerMenuPanel panel, string body, DifficultyItem difficultyItem, UnityAction a)
        {
            CreateItem(panel, null, body, false, difficultyItem, a);
        }

        private void CreateItem(PlayerMenuPanel panel, string header, string body, bool selected, UnityAction a)
        {
            CreateItem(panel, header, body, selected, _difficultyItemPrefab, a);
        }

        private void CreateItem(PlayerMenuPanel panel, string body, bool selected, UnityAction a)
        {
            CreateItem(panel, null, body, selected, a);
        }

        private string LocalizeHeader(string key)
        {
            return Localize.Key("Menu.DifficultySelect", key);
        }

        private bool HasPlayableInstrument(SongEntry entry, in Instrument instrument)
        {
            // For vocals, all players must select the same gamemode (solo/harmony).
            // We enforce the sync in NormalizeVocalSelections(), not here.
            if (instrument is Instrument.Vocals or Instrument.Harmony)
            {
                if (!entry.HasInstrument(instrument))
                    return false;
            }

            return entry.HasInstrument(instrument) || instrument switch
            {
                // Allow 5 -> 4-lane conversions to be played on 4-lane
                Instrument.FourLaneDrums or
                Instrument.ProDrums      => entry.HasInstrument(Instrument.FiveLaneDrums),
                // Allow 4 -> 5-lane conversions to be played on 5-lane
                Instrument.FiveLaneDrums => entry.HasInstrument(Instrument.ProDrums),
                _ => false
            };
        }

        private bool HasPlayableDifficulty(SongEntry entry, in Instrument instrument, in Difficulty difficulty)
        {
            // For vocals, insert special difficulties
            if (instrument is Instrument.Vocals or Instrument.Harmony)
                return difficulty is not (Difficulty.Beginner or Difficulty.ExpertPlus);

            // Otherwise, we can do this
            return entry[instrument][difficulty] || instrument switch
            {
                // Allow 5 -> 4-lane conversions to be played on 4-lane
                Instrument.FourLaneDrums or
                Instrument.ProDrums      => entry[Instrument.FiveLaneDrums][difficulty],
                // Allow 4 -> 5-lane conversions to be played on 5-lane
                Instrument.FiveLaneDrums => entry[Instrument.ProDrums][difficulty],
                _ => false
            };
        }
        public void SongSpeedEndEdit(string text)
        {
            if (!float.TryParse(text.TrimEnd('%'), NumberStyles.Number, null, out var speed))
                speed = 100;

            int intSpeed = (int) Math.Clamp(speed, 10, 5000);

            _speedInput.SetTextWithoutNotify($"{intSpeed}%");
        }

        private void SetupMenuPanels()
        {
            ClearPanels();
            EnsureMenusRoot();
            _menuTemplate = FindMenuTemplate();
            _menuTemplate.SetParent(_menusRoot, false);

            int maxPlayers = Math.Min(PlayerContainer.Players.Count, 6);
            var vocalPlayers = PlayerContainer.Players
                .Take(maxPlayers)
                .Where(player => player.Profile.GameMode == GameMode.Vocals)
                .ToList();

            PlayerMenuPanel vocalPanel = null;
            int createdIndex = 0;

            for (int i = 0; i < maxPlayers; i++)
            {
                var player = PlayerContainer.Players[i];
                bool isVocal = player.Profile.GameMode == GameMode.Vocals;
                if (isVocal && vocalPanel != null)
                {
                    _panelByPlayer[player] = vocalPanel;
                    continue;
                }

                RectTransform root;
                if (createdIndex == 0)
                {
                    root = _menuTemplate;
                }
                else
                {
                    root = Instantiate(_menuTemplate, _menusRoot);
                    root.name = $"Menu_Player_{i + 1}";
                }

                var panel = CreatePanel(player, root, createdIndex == 0);
                if (isVocal)
                {
                    panel.IsVocalGroup = true;
                    panel.VocalPlayers = vocalPlayers;
                    vocalPanel = panel;
                    foreach (var vocalPlayer in vocalPlayers)
                    {
                        _panelByPlayer[vocalPlayer] = panel;
                    }
                }
                else
                {
                    _panelByPlayer[player] = panel;
                }

                _panels.Add(panel);
                createdIndex++;
            }
        }

        private void ClearPanels()
        {
            foreach (var panel in _panels)
            {
                if (panel.NavGroup != null && panel.SelectionHandler != null)
                    panel.NavGroup.SelectionChanged -= panel.SelectionHandler;

                if (panel.Root != null && panel.Root != _menuTemplate)
                    Destroy(panel.Root.gameObject);
            }

            _panels.Clear();
            _panelByPlayer.Clear();
            _readyPlayers.Clear();
            _vocalModifierOwner = null;
        }

        private void EnsureMenusRoot()
        {
            if (_menusRoot != null) return;

            var go = new GameObject("PlayerMenusRoot", typeof(RectTransform));
            _menusRoot = go.GetComponent<RectTransform>();
            _menusRoot.SetParent(transform, false);
            _menusRoot.anchorMin = Vector2.zero;
            _menusRoot.anchorMax = Vector2.one;
            _menusRoot.offsetMin = Vector2.zero;
            _menusRoot.offsetMax = Vector2.zero;
            _menusRoot.localScale = Vector3.one;
        }

        private RectTransform FindMenuTemplate()
        {
            if (_menuTemplate != null)
                return _menuTemplate;

            Transform current = _container != null ? _container : _navGroup?.transform;
            while (current != null && current.name != "Menu")
            {
                current = current.parent;
            }

            if (current == null)
                throw new InvalidOperationException("Failed to locate menu template root named 'Menu'.");

            return (RectTransform) current;
        }

        private PlayerMenuPanel CreatePanel(YargPlayer player, RectTransform root, bool isTemplate)
        {
            var panel = new PlayerMenuPanel
            {
                Player = player,
                Root = root,
                MenuState = State.Main,
                LastMenuState = State.Main
            };

            if (isTemplate)
            {
                panel.Container = _container;
                panel.NavGroup = _navGroup;
                panel.HeaderText = _text;
                panel.Scrollbar = root.GetComponentInChildren<Scrollbar>(true);
                EnsureHeaderBaseFontSize(panel.HeaderText);
                panel.HeaderIcon = EnsureHeaderIcon(panel.HeaderText);
            }
            else
            {
                panel.NavGroup = root.GetComponentInChildren<NavigationGroup>(true);
                panel.Container = panel.NavGroup != null ? panel.NavGroup.transform : root;

                var header = root.Find("Header");
                if (header != null)
                {
                    var headerTextTransform = header.Find("Text (TMP)") ?? header;
                    panel.HeaderText = headerTextTransform.GetComponentInChildren<TextMeshProUGUI>(true);
                }

                if (panel.HeaderText == null)
                    panel.HeaderText = root.GetComponentInChildren<TextMeshProUGUI>(true);

                panel.Scrollbar = root.GetComponentInChildren<Scrollbar>(true);
                EnsureHeaderBaseFontSize(panel.HeaderText);
                panel.HeaderIcon = EnsureHeaderIcon(panel.HeaderText);
            }

            panel.SelectionHandler = (selected, origin) => UpdateForSelectionChanged(panel, selected, origin);
            if (panel.NavGroup != null)
                panel.NavGroup.SelectionChanged += panel.SelectionHandler;

            return panel;
        }
        private void LayoutPanels()
        {
            if (_panels.Count == 0 || _menuTemplate == null) return;

            int count = _panels.Count;
            int cols = count <= 3 ? count : 3;
            int rows = Mathf.CeilToInt(count / (float) cols);

            float spacingX = 40f;
            float spacingY = 40f;

            float panelWidth = _menuTemplate.rect.width;
            float panelHeight = _menuTemplate.rect.height;

            float availableWidth = _menusRoot.rect.width;
            float availableHeight = _menusRoot.rect.height;
            if (availableWidth <= 0 || availableHeight <= 0)
            {
                availableWidth = 1920f;
                availableHeight = 1080f;
            }

            float scaleX = (availableWidth - spacingX * (cols - 1)) / (panelWidth * cols);
            float scaleY = (availableHeight - spacingY * (rows - 1)) / (panelHeight * rows);
            float scale = Mathf.Min(1f, scaleX, scaleY);

            float scaledWidth = panelWidth * scale;
            float scaledHeight = panelHeight * scale;

            float totalWidth = cols * scaledWidth + spacingX * (cols - 1);
            float startX = -totalWidth / 2f + scaledWidth / 2f;
            float startY = _menuTemplate.anchoredPosition.y;

            for (int i = 0; i < _panels.Count; i++)
            {
                int row = i / cols;
                int col = i % cols;

                float x = startX + col * (scaledWidth + spacingX);
                float y = startY - row * (scaledHeight + spacingY);

                var root = _panels[i].Root;
                root.anchoredPosition = new Vector2(x, y);
                root.localScale = new Vector3(scale, scale, 1f);
            }
        }

        private void RebuildAllPlayers()
        {
            if (_panels.Count == 0) return;

            NormalizeVocalSelections();

            _globalMaxHarmonyIndex = GlobalVariables.State.CurrentSong.VocalsCount;
            foreach (var showSong in _songList)
            {
                _globalMaxHarmonyIndex = Mathf.Min(_globalMaxHarmonyIndex, showSong.VocalsCount);
            }

            foreach (var panel in _panels)
            {
                var profile = panel.Player.Profile;
                var previousInstrument = profile.CurrentInstrument;
                panel.MaxHarmonyIndex = _globalMaxHarmonyIndex;

                panel.PossibleInstruments.Clear();
                var allowedInstruments = profile.GameMode.PossibleInstrumentsForSong(GlobalVariables.State.CurrentSong);

                foreach (var instrument in allowedInstruments)
                {
                    bool invalidInstrument = false;
                    foreach (var showSong in _songList)
                    {
                        if (!HasPlayableInstrument(showSong, instrument))
                        {
                            invalidInstrument = true;
                            break;
                        }
                    }

                    if (!invalidInstrument)
                        panel.PossibleInstruments.Add(instrument);
                }

                // If the player's preferred instrument is available, set CurrentInstrument to that
                if (!panel.IsVocalGroup && panel.PossibleInstruments.Contains(profile.PreferredInstrument))
                    profile.CurrentInstrument = profile.PreferredInstrument;

                // Set the instrument to a valid one
                if (!panel.PossibleInstruments.Contains(profile.CurrentInstrument) && panel.PossibleInstruments.Count > 0)
                    profile.CurrentInstrument = panel.PossibleInstruments[0];

                if (profile.CurrentInstrument != previousInstrument)
                    SetReady(panel, false);

                // Set the harmony index to a valid one
                if (profile.HarmonyIndex >= panel.MaxHarmonyIndex)
                    profile.HarmonyIndex = 0;

                UpdatePossibleModifiers(panel);
                UpdatePossibleDifficulties(panel);

                if (panel.IsVocalGroup)
                    SyncVocalGroupSettings(panel);
            }

            StatsManager.Instance.UpdateActivePlayers();

            foreach (var panel in _panels)
            {
                UpdateForPlayer(panel);
            }
        }

        private void SetReady(PlayerMenuPanel panel, bool ready)
        {
            panel.IsReady = ready;
            foreach (var target in EnumeratePanelPlayers(panel))
            {
                if (ready)
                {
                    _readyPlayers.Add(target);
                }
                else
                {
                    _readyPlayers.Remove(target);
                }
            }
        }

        private bool AreAllPlayersReady()
        {
            foreach (var player in PlayerContainer.Players)
            {
                if (player.SittingOut) continue;

                if (!_readyPlayers.Contains(player))
                    return false;
            }

            return true;
        }

        private void TryStartGame()
        {
            if (!AreAllPlayersReady()) return;

            // If everyone is sitting out, show a warning and boot back to music library
            if (PlayerContainer.Players.All(i => i.SittingOut))
            {
                MenuManager.Instance.PopMenu();

                DialogManager.Instance.ShowMessage("Nobody's Playing!",
                    "You tried to play a song with every player sitting out.");

                return;
            }

            // Ensure all vocal players have the same modifiers active
            if (_vocalModifierOwner != null)
            {
                var primaryPlayer = _vocalModifierOwner;

                foreach (var player in PlayerContainer.Players)
                {
                    if (player.SittingOut) continue;
                    if (player == primaryPlayer) continue;

                    if (player.Profile.GameMode == GameMode.Vocals)
                        player.Profile.CopyModifiers(primaryPlayer.Profile);
                }
            }

            // This will always work (as it's set up in the input field)
            // The max speed that the game can keep up with is 5000%
            float speed = float.Parse(_speedInput.text.TrimEnd('%')) / 100f;
            speed = Mathf.Clamp(speed, 0.1f, 50.0f);
            _songSpeed = speed;
            GlobalVariables.State.SongSpeed = speed;

            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
        }

        private void UpdateWarningForPlayer(YargPlayer player)
        {
            if (player == null)
            {
                ShowWarning(null);
                return;
            }

            if (player.IsMissingMicrophone)
            {
                ShowWarning(Localize.Key("Menu.DifficultySelect.WarningVocalistNoMicrophone"));
            }
            else if (player.IsMissingInputDevice)
            {
                ShowWarning(Localize.Key("Menu.DifficultySelect.WarningPlayerNoInputDevice"));
            }
            else
            {
                ShowWarning(null);
            }
        }

        private bool TryGetPanel(YargPlayer player, out PlayerMenuPanel panel)
        {
            if (player != null && _panelByPlayer.TryGetValue(player, out panel))
                return true;

            panel = null;
            return false;
        }

        private Image EnsureHeaderIcon(TextMeshProUGUI label)
        {
            if (label == null)
                return null;

            var header = label.transform.parent;
            if (header == null)
                return null;

            var iconTransform = header.Find("HeaderIcon");
            if (iconTransform == null)
            {
                var iconObject = new GameObject("HeaderIcon", typeof(RectTransform), typeof(Image));
                iconTransform = iconObject.transform;
                iconTransform.SetParent(header, false);
            }

            var rect = (RectTransform) iconTransform;
            ConfigureHeaderIconRect(rect);

            var image = iconTransform.GetComponent<Image>();
            image.raycastTarget = false;
            image.preserveAspect = true;
            return image;
        }

        private void EnsureHeaderBaseFontSize(TextMeshProUGUI label)
        {
            if (_headerBaseFontSize > 0f || label == null) return;

            if (label.enableAutoSizing && label.fontSizeMax > 0f)
            {
                _headerBaseFontSize = label.fontSizeMax;
            }
            else
            {
                _headerBaseFontSize = label.fontSize > 0f ? label.fontSize : 20f;
            }
        }

        private void UpdateHeaderIcon(PlayerMenuPanel panel, GameMode gameMode)
        {
            if (panel.HeaderIcon == null) return;

            ConfigureHeaderIconRect(panel.HeaderIcon.rectTransform);

            string resourceName = gameMode.ToResourceName();
            if (string.IsNullOrEmpty(resourceName))
            {
                panel.HeaderIcon.enabled = false;
                return;
            }

            if (!_headerIconCache.TryGetValue(resourceName, out var sprite))
            {
                sprite = Addressables.LoadAssetAsync<Sprite>($"InstrumentIcons[{resourceName}]").WaitForCompletion();
                _headerIconCache[resourceName] = sprite;
            }

            panel.HeaderIcon.sprite = sprite;
            panel.HeaderIcon.enabled = sprite != null;
        }

        private void ConfigureHeaderIconRect(RectTransform rect)
        {
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = HeaderIconAnchoredPosition;
            rect.sizeDelta = new Vector2(HeaderIconSize, HeaderIconSize);
        }

        private void SetHeaderText(TextMeshProUGUI label, string text, bool shrinkIfLong)
        {
            const int maxCharsBeforeShrink = 60;
            const float shrinkFactor = 0.6f;

            if (label == null)
            {
                return;
            }

            EnsureHeaderBaseFontSize(label);

            var measureText = RichTextUtils.StripRichTextTags(text);

            label.enableAutoSizing = false;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Overflow;
            label.fontSize = _headerBaseFontSize;

            if (shrinkIfLong && measureText.Length > maxCharsBeforeShrink)
                label.fontSize = _headerBaseFontSize * shrinkFactor;

            var icon = label.transform.parent?.Find("HeaderIcon")?.GetComponent<RectTransform>();
            if (icon != null)
            {
                float padding = 16f;
                float leftMargin = icon.rect.width > 0f ? icon.rect.width + padding : 0f;
                label.margin = new Vector4(leftMargin, 0f, 0f, 0f);
            }

            label.text = text;
        }

        private IEnumerable<YargPlayer> EnumeratePanelPlayers(PlayerMenuPanel panel)
        {
            if (panel.IsVocalGroup && panel.VocalPlayers != null)
                return panel.VocalPlayers;

            return new[] { panel.Player };
        }

        private void AssignHarmonyIndices(PlayerMenuPanel panel)
        {
            if (!panel.IsVocalGroup || panel.VocalPlayers == null) return;

            if (panel.Player.Profile.CurrentInstrument != Instrument.Harmony)
            {
                foreach (var player in panel.VocalPlayers)
                {
                    player.Profile.HarmonyIndex = 0;
                }
                return;
            }

            if (panel.VocalPlayers.Count <= 1)
                return;

            int maxHarmony = Math.Max(panel.MaxHarmonyIndex, 1);
            int harmonyIndex = 0;
            foreach (var player in panel.VocalPlayers)
            {
                if (player.SittingOut)
                {
                    player.Profile.HarmonyIndex = 0;
                    continue;
                }

                int assigned = Math.Min(harmonyIndex, maxHarmony - 1);
                player.Profile.HarmonyIndex = (byte) assigned;
                harmonyIndex++;
            }
        }

        private void SyncVocalGroupSettings(PlayerMenuPanel panel)
        {
            if (!panel.IsVocalGroup || panel.VocalPlayers == null) return;

            var primary = panel.Player.Profile;
            foreach (var player in panel.VocalPlayers)
            {
                if (player == panel.Player) continue;

                var profile = player.Profile;
                profile.CurrentInstrument = primary.CurrentInstrument;
                profile.CurrentDifficulty = primary.CurrentDifficulty;
                profile.DifficultyFallback = primary.DifficultyFallback;
                profile.CopyModifiers(primary);
            }

            AssignHarmonyIndices(panel);
        }

        private void NormalizeVocalSelections()
        {
            var vocalPlayers = PlayerContainer.Players
                .Where(player => player.Profile.GameMode == GameMode.Vocals)
                .ToList();

            if (vocalPlayers.Count == 0) return;

            Instrument? selected = null;
            if (_lastActivePlayer != null &&
                !_lastActivePlayer.SittingOut &&
                vocalPlayers.Contains(_lastActivePlayer))
            {
                var activeInstrument = _lastActivePlayer.Profile.CurrentInstrument;
                if (activeInstrument is Instrument.Vocals or Instrument.Harmony)
                    selected = activeInstrument;
            }

            if (!selected.HasValue)
            {
                var firstVocal = vocalPlayers.FirstOrDefault();
                if (firstVocal != null)
                {
                    var instrument = firstVocal.Profile.CurrentInstrument;
                    if (instrument is Instrument.Vocals or Instrument.Harmony)
                        selected = instrument;
                }
            }

            if (!selected.HasValue) return;

            foreach (var player in vocalPlayers)
            {
                var instrument = player.Profile.CurrentInstrument;
                if (instrument != selected.Value)
                {
                    player.Profile.CurrentInstrument = selected.Value;
                    if (TryGetPanel(player, out var panel))
                        SetReady(panel, false);
                }
            }
        }
    }
}
