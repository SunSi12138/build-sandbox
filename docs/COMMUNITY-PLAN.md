# CommunityGIS implementation and acceptance

Source: SunSi12138/build-sandbox hbtool, cloned 2026-10-23. Desktop project requested by user.

## Binding decisions
- .NET 10 Native AOT Web API, SQLite, Nuxt + Nuxt UI static build. Reuse hbtool LAN listening, compression, backup, Word export.
- User approved an offline map engine after confirming AMap JS SDK is online-only. Default Jilin-1 local tiles from E:/Map. All scripts, icons, styles, WASM local. Fresh browser with external requests denied must work.
- Mobile is the same responsive UI on LAN, as hbtool. User reiterated 2026-10-23: NO dedicated phone QR photo workflow or special mobile feature. Offline means no Internet; LAN host stays available. No disconnected phone sync. This supersedes section 9 of the earlier specification.
- Full community scope from agreed functional specification; concise Chinese UI, large clear actions, usable by low literacy staff.
- Do not edit E:/Map or existing hbtool data. Copy caches. Deliver runnable AOT application and source on Desktop/CommunityGIS.
- User steering: prioritize feature development; compile and key-path checks during development, one focused integration pass at end. No repetitive validation loop or exhaustive speculative tests.

## Required acceptance matrix (unchecked until verified)
- [ ] Native AOT publish and standalone executable, static front end, no runtime/CDN dependency
- [ ] Offline map point/polygon create/edit, grid boundaries, local cache import, manageable layers, default Jilin-1; uncached coverage explicitly shown
- [ ] Places, nested units, multiple concurrent uses, move-out/new occupant history; snapshot historical inspections
- [ ] Custom inspection types/checklists, normal/issues/incomplete results, unregistered point/area, drafts and actual vs entry date
- [ ] Multiple independently tracked issues, progress/feedback/review/return, transfer/deadline change/reopen/void history, overdue
- [ ] Responsive shared desktop/mobile UI with local photo compression, phase grouping, retry, preview, no lost text on upload
- [ ] Search/filter, home todos, assigned inspection plans, consistent statistics
- [ ] DOCX and XLSX exports with exact selected scope, stable identifiers, photos, Chinese layouts
- [ ] Staff/settings/type management, history, archive/void/recovery, duplicate merge, optimistic concurrency and duplicate submission protection
- [ ] Existing grid/Excel import preview and validation
- [ ] Full backup/restore incl extension records/photos; daily snapshot; safe migration/version validation
- [ ] LAN listening setup, correct address, second instance behavior, startup errors
- [ ] Unit/API/browser/AOT tests, blocked Internet fresh browser, mobile viewport, usability iteration, real cached map visual validation

## Progress
- Repository cloned to Desktop/CommunityGIS; .NET SDK 10.0.302 and native build tools found.
- Existing Map active bundle still loads https://webapi.amap.com/maps; tile cache is separate.
- Jilin cache 3,272 files / 31,831,064 bytes. Extra map cache 68,854 files / 10,672,940,153 bytes, requires layer identification before use.
- SDK pin aligned to installed .NET 10 SDK. Dependencies installed; Jilin cache copied into project tiles/jlyh without changing source.
- Implemented OpenLayers 10.11 local engine: point/polygon drawing, modify vertices, snap to features, coordinate locate, grid/point geometry persisted, manageable local layers, TMS Jilin tile endpoint. Map initialization now watches DOM template ref because Nuxt client-only mount runs before target exists.
- Added community records/history store (units/uses/inspection types/staff/plans), optimistic revisions for community records and map shapes. UI for multiple uses/ending use, units, types/checklist, staff, plans.
- Extended inspection DB/schema 4 with inspection type and use snapshots, checklist, incomplete outcome/reason, creation time, submission key; existing SQLite backup validation includes extension tables. No production old DB migrated (new project).
- Added queryable report API, XLSX writer (BCL XML/ZIP), DOCX table reports. Existing detailed photo DOCX still inherited. New reports UI filters match export query.
- Added progress/transfer/deadline/follower operations in issue history with UI; plans and map location picker integrated. New place geometry now saved in same transaction as place.
- Frontend labels mostly adapted from hbtool; remaining wording cleanup required. Mobile remains shared UI, no special transfer workflow.

## Verified evidence so far (not final acceptance)
- dotnet publish -c Release -r win-x64 -p:PublishAot=true succeeded, release/CommunityGIS.exe generated. Native executable in work/runtime starts on 5199, listening 0.0.0.0 and enumerates 192.168.31.242.
- web/community-smoke.mjs passed one focused flow: create grid/place/polygon/use, inspection+issue, feedback/review closed, incomplete outcome, end-use historical snapshot, DOCX/XLSX download.
- work/checks/result.json: no page errors, no external requests on desktop place/map and mobile inspection. Map initially blank due client-only ref timing; fixed with watch(element), targeted check shows canvas + controls; visually confirmed Jilin imagery and polygon under blocked external requests. work/checks/map-desktop.png and inspection-mobile.png inspected.
- Export archives valid XML and XLSX row counts correct; actual Word/WPS layout not yet reviewed.
- Latest source dotnet build and Nuxt typecheck pass after plans/progress/location picker. Native smoke binary predates those latest changes, do NOT claim final native package includes them.

## Next work / known incomplete areas
- Complete accepted scope: Excel/grid import preview/apply, duplicates merge, archive/void/recovery, staff handoff, richer statistics and plan export/edit, use photos/classification fields, task/checklist reminders by type, historical snapshots and archive integrity.
- Map: WGS84/GCJ02 conversion UI and old grid coordinate import check; block invalid crossing polygons; point coordinate manual edit must reconcile stored polygon (current null Shape leaves old geometry). Improve shape selection/edit UX, save flags and grid assignment; drawing snap implemented but not yet exercised in browser.
- Core mutation concurrency for points/inspections/issues still inherited hbtool; add optimistic update checks/history where required, avoid stale edits. Issue operations keep history but aren't yet included in detailed Word history.
- Generic record store needs stronger domain checks (unit hierarchy, dates, type refs, ended use policy). JsonDocument.Parse(...).RootElement.Clone in ReadCommunityRecord currently not disposed: replace with scoped helper.
- Use photos not yet implemented (PhotoIds stored only in model); protect photo association/cleanup and include in backup.
- Local drafts are hbtool browser-only; accepted shared-server draft scope needs deliberate implementation; no dedicated phone draft transfer UI per user clarification.
- Scene normal/incomplete checklist consistency needs guard; plans preselected type needs checklist initialization; incomplete counting/filter UI needs finishing (not green/normal).
- Existing SeedDemo references old environmental point types and must be adapted before using it. Existing automated tests contain old schema/type/branding assertions; update only meaningful tests, don't repeat huge suites unnecessarily.
- Existing generic point deletion must not orphan units/uses/geometry; archive/merge needed. Old Word titles/placeholders some environmental wording remains.
- UI cleanup: grid vs community labels, expose new pages consistently, suppress technical coordinate help unless needed; check controls after large mobile forms.
- Package latest code and static assets + tiles into release; release currently has EXE only. Add build/run instructions and double-click launch. Copy clean app (no smoke data). Keep test data under work/runtime.
- Native runtime currently running via exec session 27187 at http://127.0.0.1:5199 (PID can be verified from process path). Source edits do not update it automatically. No need to restart until next integration milestone.
- User says feature development first; do only necessary checks, then one scoped final integration pass. Goal remains ACTIVE and incomplete.
