# Third-party source

## Radarr frontend components

Grimarr adapts source from [Radarr/Radarr](https://github.com/Radarr/Radarr), revision [`a96bf7c3ecb23cb76993770727285dc17a613fbc`](https://github.com/Radarr/Radarr/tree/a96bf7c3ecb23cb76993770727285dc17a613fbc).

Original authors: the Radarr contributors. License: GNU General Public License version 3, reproduced in this repository's [LICENSE](LICENSE).

Adapted files under `frontend/src/components/arr/` originate from:

- `frontend/src/Components/Page/Sidebar/PageSidebarItem.tsx` and `.css`
- `frontend/src/Components/Page/Toolbar/PageToolbar.tsx` and `.css`
- `frontend/src/Components/Page/Toolbar/PageToolbarButton.tsx` and `.css`
- `frontend/src/Components/Loading/LoadingIndicator.tsx` and `.css`
- `frontend/src/Components/Page/Header/PageHeader.tsx` and `.css`

Grimarr changes: local navigation and event callbacks, Lucide icon and button adapters, native CSS modules/variables in place of Radarr's build aliases and preprocessor variables, responsive sizing, audiobook terminology, a green accent theme, and removal of Radarr-specific Redux state, movie search, donations, and account menus. Grimarr's own C# API remains the backend; no Radarr server or movie API is required.

The complete Radarr application has not been imported. These adapted presentation components are used by Grimarr's audiobook library, toolbar, navigation, and loading state. Radarr's name and logos are not used as Grimarr branding.
