# Changelog

## 1.1

- Added controls to close and re-enable individual Mods from the installed Mod list.
- Added a prerequisite confirmation window before downloading Mods that require other projects.
- Prerequisites are downloaded before the selected Mod and existing verified files are reused.
- Marked the launcher as a formal release and refined the default home artwork layout.

## 1.0

- Released Kumo as a self-contained Windows x64 single executable.
- Unified the public application name, executable, version metadata, and data directory under Kumo.
- Added cached Minecraft profile and appearance assets so the customization screen opens immediately.
- Added 3D skin and cape customization with drag rotation and whole-panel wheel scrolling.
- Added automatic settings persistence and migration from the launcher's legacy data directory.
- Added global Minecraft display, performance, and keybinding settings shared by every launched version.
- Added Modrinth and CurseForge catalogs, Chinese metadata, dependency-aware installation, and complete cover images.
- Added PCL2, Modrinth, and CurseForge Modpack import, including drag-and-drop installation.
- Added version isolation, automatic Java management, and Fabric, Quilt, Forge, NeoForge, OptiFine, and OptiFabric support.
- Added the final Kumo slime icon and polished window, account, version, Mod, and appearance interfaces.
- Fixed first-run startup failure when the settings file contained an empty data directory.

## 0.21.5-beta

- Resolved missing Chinese descriptions for remaining Mod results in the background and updated cards in place.

## 0.21.4-beta

- Switched to the secondary source when it has a larger compatible catalog, preserving multi-page Mod browsing.

## 0.21.3-beta

- Filled short Mod result pages from the secondary source while keeping the faster measured source primary.

## 0.21.2-beta

- Removed manual Mod source buttons and added automatic latency-based source selection.

## 0.21.1-beta

- Unified and emphasized the Mod instance and search field labels.

## 0.21.0-beta

- Replaced the MC百科 source entry with CurseForge.
- Added CurseForge project search, compatible-file selection, cover loading, and in-app installation.
- Matched Modrinth and CurseForge results with MC百科 Chinese descriptions while keeping MC百科 out of the source selector.

## 0.20.6-beta

- Enlarged the background of the header account pill while keeping its contents unchanged.

## 0.20.5-beta

- Moved the header account pill's gradient edge highlight to the hover state.

## 0.20.4-beta

- Aligned the latest-release label and log button in the launch activity header.

## 0.20.3-beta

- Reworked the home hero into a centered primary action layout.
- Moved the launch button below the version selector and centered it.

## 0.20.2-beta

- Kept the version status card fixed and fully visible at the bottom of the loader panel.
- Made the loader and installed-instance cards scroll independently above the status card.

## 0.20.1-beta

- Fixed loader version drop-downs rendering the full option record instead of the version label.
- Limited loader version labels to the plain version number.

## 0.20.0-beta

- Added a unified loader installer with Fabric, Quilt, Forge, and NeoForge selection.
- Added selectable loader versions loaded from official metadata and official mirrors.
- Added unattended Forge and NeoForge installer support with mirrored dependency preparation.
- Extended Modrinth filtering and installation to Forge and NeoForge instances.

## 0.19.16-beta

- Replaced the header account button's default WPF chrome with a custom rounded account pill.
- Added a theme gradient border and removed the system blue highlight and focus frame.

## 0.19.15-beta

- Replaced the saved-account ListBox with a non-selectable ItemsControl to remove Windows selection highlights and dotted focus frames.
- Added a theme-aware gradient border to the active account card.

## 0.19.14-beta

- Removed the custom wheel-speed constants and now follows the Windows wheel-lines setting.
- Kept pixel scrolling and frame-synced spring motion so content does not snap to items.

## 0.19.13-beta

- Increased each wheel step from 72 px to 108 px across pages and lists.
- Raised spring responsiveness for quicker settling without removing the animation.

## 0.19.12-beta

- Removed the extra speed braking applied at the top and bottom of scroll areas.
- Kept the normal spring easing without adding boundary-specific slowdown.

## 0.19.11-beta

- Applied the same smooth-scroll behavior to installed-version ComboBox drop-downs.
- Removed the remaining speed mismatch between native drop-down scrolling and animated page scrolling.

## 0.19.10-beta

- Increased the wheel step from 48 px to the previously preferred 72 px.

## 0.19.9-beta

- Matched smooth scrolling to the native WPF wheel distance used by version drop-downs.
- Replaced the fixed 108 px step with the system wheel-line setting at 16 px per line.

## 0.19.8-beta

- Matched smooth wheel scrolling to the three-row speed used by the installed-version selector.

## 0.19.7-beta

- Fixed smooth scrolling not attaching when the initially hidden settings page first became visible.
- Added visibility-aware attachment for hidden page scroll containers.

## 0.19.6-beta

- Fixed the settings page deferring wheel input to ordinary text and combo boxes, which bypassed smooth scrolling.
- Added velocity braking near content boundaries to prevent overshoot and abrupt stops.

## 0.19.5-beta

- Prevented native scrolling from taking over when a smooth-scroll list reaches its top or bottom.
- Let the spring animation decelerate naturally at content boundaries instead of snapping to the edge.

## 0.19.4-beta

- Replaced direct easing with a critically damped spring for smoother acceleration and deceleration.
- Preserved scroll velocity across rapid wheel input to avoid visible restarts.

## 0.19.3-beta

- Moved smooth scrolling from a fixed timer to frame-synced rendering.
- Made easing frame-rate independent for consistent motion on 60 Hz and 120 Hz displays.

## 0.19.2-beta

- Fixed wheel scrolling using item counts instead of pixels, which made one wheel notch reach the list bottom.
- Set virtualized lists to pixel scrolling and limited each wheel notch to a 72 px animated step.

## 0.19.1-beta

- Added smooth wheel scrolling to page, version, Mod, and account lists.
- Kept nested scrolling controls and text inputs on their own scroll paths.

## 0.19.0-beta

- Preloaded popular compatible Mods when the launcher starts or the Mod workshop opens.
- Kept the Mod catalog visible without requiring a search and refreshed it automatically when the instance or source changed.

## 0.18.5-beta

- Switched vanilla versions to the lighter official grass-block artwork used by newer version rows.
- Improved loader icon scaling so the grass block stays bright and clear at small sizes.

## 0.18.4-beta

- Replaced the creeper app tile with the official complete isometric grass-block icon.
- Imported the 300 px vanilla version artwork from the locally installed Minecraft package.

## 0.18.3-beta

- Changed the vanilla icon from the grass texture to the complete Minecraft Launcher app icon.
- Imported the official taskbar artwork from the locally installed Microsoft Store launcher.

## 0.18.2-beta

- Added the official Minecraft grass-block icon for vanilla versions.
- Extracted and cached the icon from the installed client JAR without bundling Mojang assets.

## 0.18.1-beta

- Fixed a launcher startup failure caused by loader icon converter resource lookup order.
- Added an application resource initialization regression test.

## 0.18.0-beta

- Added official Fabric, Quilt, Minecraft Forge, and NeoForge loader artwork.
- Showed loader icons in every installed-version selector and the My Versions list.
- Recognized existing Forge and NeoForge profiles so their matching icons can be displayed.

## 0.17.0-beta

- Added light, dark, and system-following launcher themes.
- Applied theme changes immediately and remembered the choice across launches.
- Added theme-aware backgrounds for transparent Mod cover images.

## 0.16.2-beta

- Fixed repeated Mod searches returning raw cached WebP images instead of display PNGs.
- Moved cover cache to `covers-v3` to invalidate affected Iris artwork.

## 0.16.1-beta

- Replaced generated offline faces with Mojang's official default Steve and Alex skins.
- Reuse installed client JAR resources and download the official client only when necessary.

## 0.16.0-beta

- Added Steve and Alex skin-model selection for offline accounts.
- Added separate local avatar choices for each offline skin model.

## 0.15.1-beta

- Fixed official Minecraft skin URLs that use the historical HTTP scheme.
- Refresh Microsoft player skins from Mojang on launcher startup.

## 0.15.0-beta

- Replaced letter placeholders with Minecraft player heads.
- Added local skin cropping and cached avatars for Microsoft accounts.
- Added deterministic pixel-art heads for offline accounts.

## 0.14.0-beta

- Fixed the Microsoft sign-in dialog close button after a failed login.
- Added Escape-key handling for modal dialogs.
- Removed screenshot and sample-data hooks from the application binary.
- Added open-source project documentation and CI configuration.
- Moved smoke tests under the project root and centralized release versioning.

## Earlier development builds

- Added official Minecraft installation, Java management, Fabric and Quilt,
  Microsoft and offline accounts, MC百科 and Modrinth sources, cover caching,
  instance management, and resumable downloads.
