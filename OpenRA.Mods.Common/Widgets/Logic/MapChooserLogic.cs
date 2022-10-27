#region Copyright & License Information
/*
 * Copyright 2007-2022 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class MapChooserLogic : ChromeLogic
	{
		[TranslationReference]
		static readonly string AllMaps = "all-maps";
		readonly string allMaps;

		[TranslationReference]
		static readonly string NoMatches = "no-matches";

		[TranslationReference("players")]
		static readonly string Players = "player-players";

		[TranslationReference("author")]
		static readonly string CreatedBy = "created-by";

		[TranslationReference]
		static readonly string MapSizeHuge = "map-size-huge";

		[TranslationReference]
		static readonly string MapSizeLarge = "map-size-large";

		[TranslationReference]
		static readonly string MapSizeMedium = "map-size-medium";

		[TranslationReference]
		static readonly string MapSizeSmall = "map-size-small";

		[TranslationReference("map")]
		static readonly string MapDeletionFailed = "map-deletion-failed";

		[TranslationReference]
		static readonly string DeleteMapTitle = "delete-map-title";

		[TranslationReference("title")]
		static readonly string DeleteMapPrompt = "delete-map-prompt";

		[TranslationReference]
		static readonly string DeleteMapAccept = "delete-map-accept";

		[TranslationReference]
		static readonly string DeleteAllMapsTitle = "delete-all-maps-title";

		[TranslationReference]
		static readonly string DeleteAllMapsPrompt = "delete-all-maps-prompt";

		[TranslationReference]
		static readonly string DeleteAllMapsAccept = "delete-all-maps-accept";

		readonly Widget widget;
		readonly DropDownButtonWidget gameModeDropdown;
		readonly DropDownButtonWidget orderByDropdown;
		readonly ModData modData;

		MapClassification currentTab;

		readonly Dictionary<MapClassification, ScrollPanelWidget> scrollpanels = new Dictionary<MapClassification, ScrollPanelWidget>();

		readonly Dictionary<MapClassification, MapPreview[]> tabMaps = new Dictionary<MapClassification, MapPreview[]>();
		string[] visibleMaps;

		string selectedUid;
		readonly Action<string> onSelect;

		string category;
		string mapFilter;

		Func<MapPreview, long> orderByFunc;

		[ObjectCreator.UseCtor]
		internal MapChooserLogic(Widget widget, ModData modData, string initialMap,
			MapClassification initialTab, Action onExit, Action<string> onSelect, MapVisibility filter)
		{
			this.widget = widget;
			this.modData = modData;
			this.onSelect = onSelect;

			allMaps = modData.Translation.GetString(AllMaps);

			var approving = new Action(() =>
			{
				Ui.CloseWindow();
				onSelect?.Invoke(selectedUid);
			});

			var canceling = new Action(() => { Ui.CloseWindow(); onExit(); });

			var okButton = widget.Get<ButtonWidget>("BUTTON_OK");
			okButton.Disabled = this.onSelect == null;
			okButton.OnClick = approving;
			widget.Get<ButtonWidget>("BUTTON_CANCEL").OnClick = canceling;

			gameModeDropdown = widget.GetOrNull<DropDownButtonWidget>("GAMEMODE_FILTER");
			orderByDropdown = widget.GetOrNull<DropDownButtonWidget>("ORDERBY");

			var itemTemplate = widget.Get<ScrollItemWidget>("MAP_TEMPLATE");
			widget.RemoveChild(itemTemplate);

			var mapFilterInput = widget.GetOrNull<TextFieldWidget>("MAPFILTER_INPUT");
			if (mapFilterInput != null)
			{
				mapFilterInput.TakeKeyboardFocus();
				mapFilterInput.OnEscKey = _ =>
				{
					if (mapFilterInput.Text.Length == 0)
						canceling();
					else
					{
						mapFilter = mapFilterInput.Text = null;
						EnumerateMaps(currentTab, itemTemplate);
					}

					return true;
				};
				mapFilterInput.OnEnterKey = _ => { approving(); return true; };
				mapFilterInput.OnTextEdited = () =>
				{
					mapFilter = mapFilterInput.Text;
					EnumerateMaps(currentTab, itemTemplate);
				};
			}

			var randomMapButton = widget.GetOrNull<ButtonWidget>("RANDOMMAP_BUTTON");
			if (randomMapButton != null)
			{
				randomMapButton.OnClick = () =>
				{
					var uid = visibleMaps.Random(Game.CosmeticRandom);
					selectedUid = uid;
					scrollpanels[currentTab].ScrollToItem(uid, smooth: true);
				};
				randomMapButton.IsDisabled = () => visibleMaps == null || visibleMaps.Length == 0;
			}

			var deleteMapButton = widget.Get<ButtonWidget>("DELETE_MAP_BUTTON");
			deleteMapButton.IsDisabled = () => currentTab != MapClassification.User;
			deleteMapButton.IsVisible = () => currentTab == MapClassification.User;
			deleteMapButton.OnClick = () =>
			{
				DeleteOneMap(selectedUid, (string newUid) =>
				{
					RefreshMaps(currentTab, filter);
					EnumerateMaps(currentTab, itemTemplate);
					if (tabMaps[currentTab].Length == 0)
						SwitchTab(modData.MapCache[newUid].Class, itemTemplate);
				});
			};

			var deleteAllMapsButton = widget.Get<ButtonWidget>("DELETE_ALL_MAPS_BUTTON");
			deleteAllMapsButton.IsVisible = () => currentTab == MapClassification.User;
			deleteAllMapsButton.OnClick = () =>
			{
				DeleteAllMaps(visibleMaps, (string newUid) =>
				{
					RefreshMaps(currentTab, filter);
					EnumerateMaps(currentTab, itemTemplate);
					SwitchTab(modData.MapCache[newUid].Class, itemTemplate);
				});
			};

			SetupMapTab(MapClassification.User, filter, "USER_MAPS_TAB_BUTTON", "USER_MAPS_TAB", itemTemplate);
			SetupMapTab(MapClassification.System, filter, "SYSTEM_MAPS_TAB_BUTTON", "SYSTEM_MAPS_TAB", itemTemplate);

			if (initialMap == null && tabMaps.ContainsKey(initialTab) && tabMaps[initialTab].Length > 0)
			{
				selectedUid = Game.ModData.MapCache.ChooseInitialMap(tabMaps[initialTab].Select(mp => mp.Uid).First(),
					Game.CosmeticRandom);
				currentTab = initialTab;
			}
			else
			{
				selectedUid = Game.ModData.MapCache.ChooseInitialMap(initialMap, Game.CosmeticRandom);
				currentTab = tabMaps.Keys.FirstOrDefault(k => tabMaps[k].Select(mp => mp.Uid).Contains(selectedUid));
			}

			SwitchTab(currentTab, itemTemplate);
		}

		void SwitchTab(MapClassification tab, ScrollItemWidget itemTemplate)
		{
			currentTab = tab;
			EnumerateMaps(tab, itemTemplate);
		}

		void RefreshMaps(MapClassification tab, MapVisibility filter)
		{
			tabMaps[tab] = modData.MapCache.Where(m => m.Status == MapStatus.Available &&
				m.Class == tab && (m.Visibility & filter) != 0).ToArray();
		}

		void SetupMapTab(MapClassification tab, MapVisibility filter, string tabButtonName, string tabContainerName, ScrollItemWidget itemTemplate)
		{
			var tabContainer = widget.Get<ContainerWidget>(tabContainerName);
			tabContainer.IsVisible = () => currentTab == tab;
			var tabScrollpanel = tabContainer.Get<ScrollPanelWidget>("MAP_LIST");
			tabScrollpanel.Layout = new GridLayout(tabScrollpanel);
			scrollpanels.Add(tab, tabScrollpanel);

			var tabButton = widget.Get<ButtonWidget>(tabButtonName);
			tabButton.IsHighlighted = () => currentTab == tab;
			tabButton.IsVisible = () => tabMaps[tab].Length > 0;
			tabButton.OnClick = () => SwitchTab(tab, itemTemplate);

			RefreshMaps(tab, filter);
		}

		void SetupGameModeDropdown(MapClassification tab, DropDownButtonWidget gameModeDropdown, ScrollItemWidget itemTemplate)
		{
			if (gameModeDropdown != null)
			{
				var categoryDict = new Dictionary<string, int>();
				foreach (var map in tabMaps[tab])
				{
					foreach (var category in map.Categories)
					{
						categoryDict.TryGetValue(category, out var count);
						categoryDict[category] = count + 1;
					}
				}

				// Order categories alphabetically
				var categories = categoryDict
					.Select(kv => (Category: kv.Key, Count: kv.Value))
					.OrderBy(p => p.Category)
					.ToList();

				// 'all game types' extra item
				categories.Insert(0, (null as string, tabMaps[tab].Length));

				Func<(string Category, int Count), string> showItem = x => (x.Category ?? allMaps) + $" ({x.Count})";

				Func<(string Category, int Count), ScrollItemWidget, ScrollItemWidget> setupItem = (ii, template) =>
				{
					var item = ScrollItemWidget.Setup(template,
						() => category == ii.Category,
						() => { category = ii.Category; EnumerateMaps(tab, itemTemplate); });
					item.Get<LabelWidget>("LABEL").GetText = () => showItem(ii);
					return item;
				};

				gameModeDropdown.OnClick = () =>
					gameModeDropdown.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", 210, categories, setupItem);

				gameModeDropdown.GetText = () =>
				{
					var item = categories.FirstOrDefault(m => m.Category == category);
					if (item == default((string, int)))
						item.Category = modData.Translation.GetString(NoMatches);

					return showItem(item);
				};
			}
		}

		void SetupOrderByDropdown(MapClassification tab, ScrollItemWidget itemTemplate)
		{
			if (orderByDropdown == null) return;

			var orderByDict = new Dictionary<string, Func<MapPreview, long>>()
			{
				{ "Players", m => m.PlayerCount },
				{ "Map Date", m => -m.ModifiedDate.Ticks }
			};

			if (orderByFunc == null)
				orderByFunc = orderByDict["Players"];

			Func<string, ScrollItemWidget, ScrollItemWidget> setupItem = (o, template) =>
			{
				var item = ScrollItemWidget.Setup(template,
					() => orderByFunc == orderByDict[o],
					() => { orderByFunc = orderByDict[o]; EnumerateMaps(tab, itemTemplate); });
				item.Get<LabelWidget>("LABEL").GetText = () => o;

				return item;
			};

			orderByDropdown.OnClick = () =>
				orderByDropdown.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", 500, orderByDict.Keys, setupItem);

			orderByDropdown.GetText = () =>
				orderByDict.FirstOrDefault(m => m.Value == orderByFunc).Key;
		}

		void EnumerateMaps(MapClassification tab, ScrollItemWidget template)
		{
			SetupOrderByDropdown(currentTab, template);

			if (!int.TryParse(mapFilter, out var playerCountFilter))
				playerCountFilter = -1;

			var maps = tabMaps[tab]
				.Where(m => category == null || m.Categories.Contains(category))
				.Where(m => mapFilter == null ||
					(m.Title != null && m.Title.IndexOf(mapFilter, StringComparison.OrdinalIgnoreCase) >= 0) ||
					(m.Author != null && m.Author.IndexOf(mapFilter, StringComparison.OrdinalIgnoreCase) >= 0) ||
					m.PlayerCount == playerCountFilter)
				.OrderBy(orderByFunc)
				.ThenBy(m => m.Title);

			scrollpanels[tab].RemoveChildren();
			foreach (var loop in maps)
			{
				var preview = loop;

				// Access the minimap to trigger async generation of the minimap.
				preview.GetMinimap();

				Action dblClick = () =>
				{
					if (onSelect != null)
					{
						Ui.CloseWindow();
						onSelect(preview.Uid);
					}
				};

				var item = ScrollItemWidget.Setup(preview.Uid, template, () => selectedUid == preview.Uid,
					() => selectedUid = preview.Uid, dblClick);
				item.IsVisible = () => item.RenderBounds.IntersectsWith(scrollpanels[tab].RenderBounds);

				var titleLabel = item.Get<LabelWithTooltipWidget>("TITLE");
				if (titleLabel != null)
				{
					WidgetUtils.TruncateLabelToTooltip(titleLabel, preview.Title);
				}

				var previewWidget = item.Get<MapPreviewWidget>("PREVIEW");
				previewWidget.Preview = () => preview;

				var detailsWidget = item.GetOrNull<LabelWidget>("DETAILS");
				if (detailsWidget != null)
				{
					var type = preview.Categories.FirstOrDefault();
					var details = "";
					if (type != null)
						details = type + " ";

					details += modData.Translation.GetString(Players, Translation.Arguments("players", preview.PlayerCount));
					detailsWidget.GetText = () => details;
				}

				var authorWidget = item.GetOrNull<LabelWithTooltipWidget>("AUTHOR");
				if (authorWidget != null && !string.IsNullOrEmpty(preview.Author))
					WidgetUtils.TruncateLabelToTooltip(authorWidget, modData.Translation.GetString(CreatedBy, Translation.Arguments("author", preview.Author)));

				var sizeWidget = item.GetOrNull<LabelWidget>("SIZE");
				if (sizeWidget != null)
				{
					var size = preview.Bounds.Width + "x" + preview.Bounds.Height;
					var numberPlayableCells = preview.Bounds.Width * preview.Bounds.Height;
					if (numberPlayableCells >= 120 * 120) size += " " + modData.Translation.GetString(MapSizeHuge);
					else if (numberPlayableCells >= 90 * 90) size += " " + modData.Translation.GetString(MapSizeLarge);
					else if (numberPlayableCells >= 60 * 60) size += " " + modData.Translation.GetString(MapSizeMedium);
					else size += " " + modData.Translation.GetString(MapSizeSmall);
					sizeWidget.GetText = () => size;
				}

				scrollpanels[tab].AddChild(item);
			}

			if (tab == currentTab)
			{
				visibleMaps = maps.Select(m => m.Uid).ToArray();
				SetupGameModeDropdown(currentTab, gameModeDropdown, template);
			}

			if (visibleMaps.Contains(selectedUid))
				scrollpanels[tab].ScrollToItem(selectedUid);
		}

		string DeleteMap(string map)
		{
			try
			{
				modData.MapCache[map].Delete();
				if (selectedUid == map)
					selectedUid = Game.ModData.MapCache.ChooseInitialMap(tabMaps[currentTab].Select(mp => mp.Uid).FirstOrDefault(),
						Game.CosmeticRandom);
			}
			catch (Exception ex)
			{
				TextNotificationsManager.Debug(modData.Translation.GetString(MapDeletionFailed, Translation.Arguments("map", map)));
				Log.Write("debug", ex.ToString());
			}

			return selectedUid;
		}

		void DeleteOneMap(string map, Action<string> after)
		{
			ConfirmationDialogs.ButtonPrompt(modData,
				title: DeleteMapTitle,
				text: DeleteMapPrompt,
				textArguments: Translation.Arguments("title", modData.MapCache[map].Title),
				onConfirm: () =>
				{
					var newUid = DeleteMap(map);
					after?.Invoke(newUid);
				},
				confirmText: DeleteMapAccept,
				onCancel: () => { });
		}

		void DeleteAllMaps(string[] maps, Action<string> after)
		{
			ConfirmationDialogs.ButtonPrompt(modData,
				title: DeleteAllMapsTitle,
				text: DeleteAllMapsPrompt,
				onConfirm: () =>
				{
					foreach (var map in maps)
						DeleteMap(map);

					after?.Invoke(Game.ModData.MapCache.ChooseInitialMap(null, Game.CosmeticRandom));
				},
				confirmText: DeleteAllMapsAccept,
				onCancel: () => { });
		}
	}
}
