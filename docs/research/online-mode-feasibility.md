# Online mode: what it uses and whether we can revive it

Date 2026-10-05. Research only: no code, manifest or build changed. The online
spike the same day (`.scratch/online-spike/`, branch `online-spike`) tested it on
the running game: §6 has its results, and they close the open questions below.
Addresses are base XEX v5 in `analysis/xex/default.pe` (file offset = address −
0x82000000), except §1.5, which gives TU2 addresses (ADR-0022, online-latency
ticket 01). Disassembly is from `python scripts/disasm.py`. Call and string
cross-references come from a throwaway `lis`/`addi` and `bl` scanner over
`.text`, so a function reached only through a vtable shows no caller. ReXGlue
paths are relative to `.tools/rexglue-source` (`f5337cdc` plus `patches/`).
Game config was read from `assets/Game/Config/Xenon/Cooked/Coalesced.ini`
(UE3 coalesced format, three files inside). Related decision: ADR-0008
(online stubbed in v1).

**[V]** marks a fact checked against a primary source. **[I]** marks an
inference.

## Answer

- **Spike result (§6): go is technically supported.** With the Live gates forced
  in the SDK, the game reaches its own Rendez-Vous login and sends
  `Authentication.Login` over PRUDP v0. Player Match needs only login,
  `SecureConnection.Register` and telemetry stubs from Rendez-Vous. Determinism
  is still untested, because SyncSim only starts in an online session.

- **Three stacks, not one.** The online mode stacks three layers:
  1. **Xbox Live** for sign-in, matchmaking (`XSession*` through UE3's
     `OnlineSubsystemLive`), arbitrated leaderboards, voice, invites and
     Marketplace.
  2. **Quazal Net-Z** as the peer-to-peer game transport. It runs PRUDP over
     XNet UDP/VDP sockets and uses Net-Z **SyncSim**, an input-lockstep
     simulation with checksums.
  3. **Quazal Rendez-Vous** as a backend that THQ/Quazal hosted. The game
     reaches it through an Xbox **LSP** (title-managed server behind the Live
     security gateway), with service ID `0x545107FD`. **[V]** for all three.
- **The match transport only sends controller input.** The game's Net-Z DDL
  declares a `SyncInputHandler` sync object that carries `JoypadAxes`
  (`mLSAxisX/Y`, `mRSAxisX/Y`, int16) and a `ButtonBitmap` per tick. It also
  carries Simulation, Station and User checksums for desync detection. **[V]**
  So the 360 game was built to be deterministic given inputs. **[I]**
- **No System Link mode.** No user-visible string anywhere mentions System
  Link or LAN. The engine has system-link plumbing (UE3 `SLS_*` enum,
  `SystemLinkAnnouncePort=14001`, Net-Z calls `XNetSetSystemLinkPort`), but
  the UI offers only Quick, Player and Ranked match over Xbox LIVE. **[V]** for
  the strings, **[I]** for "no LAN mode".
- **ReXGlue cannot run any of it today.** Sign-in reports "signed in locally",
  every privilege is denied, and the XNet calls that matter are bare stubs
  (`XNetConnect`, `XNetServerToInAddr`, `XNetCreateKey`, `XNetRegisterKey`,
  `XNetQosLookup` and others). A bare stub returns the wrapper's `r3 = 1`, which
  the game reads as an error. `WSARecvFrom` always fails, and `XSession*`
  messages return success but fill nothing in. **[V]**
- **Not on any compatibility list.** WWE All Stars (`54510866`) is absent from
  the Xenia Canary netplay fork's compatibility list. SvR 2010 and SvR 2011
  work there on public servers. **[V]**
- **Verdict: feasible, but a multi-month side project.** The realistic path is
  to make the game's *own* online mode work: real XNet and XSession in
  ReXGlue (port them from the netplay fork, BSD-licensed like ReXGlue), a
  session server (Xenia Web Services or our own), and a way past the
  Rendez-Vous login (a minimal RV server, or a host hook). A first spike
  of about a week answers the one open question that decides the cost: how
  much of Rendez-Vous the online flow really needs.
  Original leaderboard data, DLC sales and THQ telemetry cannot be brought
  back.

## 1. What the online mode uses

### 1.1 Engine and config [V]

- UE3: `DefaultOnlineSubsystemName=OnlineSubsystemLive` (Coalesced.ini, offset
  `0x1076`) and `NetworkDevice=XeDrv.IpNetDriverXe`. The image contains about
  70 `intUOnlineSubsystemLiveexec*` natives, at `0x821316E9`–`0x82133239`
  (UTF-16). Examples: `CreateOnlineGame`, `FindOnlineGames`,
  `RegisterForArbitration`, `WriteOnlineStats`, `WriteTrueSkillForDroppedPlayer`.
- `[XeWinSock] bUseVDP=True`, `MaxDgramSockets=64` (offset `0xB75D`).
- `[OnlineSubsystemLive.OnlineSubsystemLive] SystemLinkAnnouncePort=14001,
  MaxRemoteTalkers=30` (offset `0xB7D9`).
- `[Quazal]` (offset `0xB8E9`) sets `ServerAddress=allstarsx360devm.quazal.net`
  and `ServerPort=32480`. The live address `allstarsx360live.quazal.net` and
  port 11705 are present but commented out. The section also holds an
  `AccessKey` and a `GuestAccountPassword`. Their values are deliberately not
  copied here. The shipped config pointing at a `devm` host is odd. On the
  360 the LSP path below resolves the server through Live, so this hostname
  may never be used. **[I]**
- The game reads those four keys in `0x82B12818` and in `0x82C24958`, via
  `GConfig` vtable calls with `"Quazal"` (UTF-16 at `0x82205ADC`), `AccessKey`,
  `GuestAccountPassword`, `ServerAddress` and `ServerPort`
  (`0x82205A68`–`0x82205AC8`).

### 1.2 Imports [V]

From the generated import table (`generated/default/wweallstars_init.cpp`,
thunks `0x834FFC84`–`0x83500A24`), the title imports:

- **XNet:** `XNetStartup/Cleanup/Random`, `XNetCreateKey/RegisterKey/UnregisterKey`,
  `XNetXnAddrToInAddr`, `XNetServerToInAddr`, `XNetUnregisterInAddr`,
  `XNetConnect`, `XNetGetConnectStatus`, `XNetDnsLookup/Release`,
  `XNetQosListen/Lookup/Release`, `XNetGetTitleXnAddr`,
  `XNetGetEthernetLinkStatus`, `XNetSetSystemLinkPort`, `XNetLogonGetTitleID`.
- **Sockets:** `socket/bind/connect/listen/accept/select/send/recv/sendto/recvfrom`,
  `WSASendTo/WSARecvFrom/WSAGetOverlappedResult`, WSA events.
- **Sessions:** `XamSessionCreateHandle`, `XamSessionRefObjByHandle`,
  `XMsgStartIORequest(Ex)`, `XMsgInProcessCall`. The XDK `XSession*` library is
  statically linked. Its message stubs sit at `0x82F82138`–`0x82F82EB0` and call
  `XamSessionRefObjByHandle` plus `XMsgStartIORequest`.
- **User and Live:** `XamUserGetSigninState`, `XamUserCheckPrivilege`,
  `XamUserAreUsersFriends`, `XamUserCreateStatsEnumerator`,
  `XamUserGetMembershipTierFromXUID`, `XamUserGetOnlineCountryFromXUID`,
  `XamVoiceCreate/SubmitPacket/HeadsetPresent`, plus `XamShowSigninUI`,
  `XamShowFriendsUI`, `XamShowGameInviteUI`, `XamShowMarketplaceUI`,
  `XamShowGamerCardUIForXUID` and `XamShowPlayerReviewUI`.

Each `NetDll_*` import is called through a 5-instruction wrapper that shifts the
arguments and sets `r3 = 1` (the "caller" argument). For example, `0x82E9FDF0`
is the `XNetServerToInAddr` wrapper and `0x82E9FFD0` the `socket` wrapper.

Who calls what:

| Import | Wrapper | Callers (function start) | Owner [I] |
|---|---|---|---|
| XNetServerToInAddr, XNetConnect | 0x82E9FDF0, 0x82E9FE18 | 0x830E8F68 only | Quazal `JobLSPLogin` |
| XNetGetConnectStatus | 0x82E9FE28 | 0x830E8DE8 only | Quazal `JobLSPLogin` |
| XNetCreateKey | 0x82E9FDA8 | 0x829DECD0, 0x830766A0 | UE3 OSL host, Net-Z |
| XNetRegisterKey | 0x82E9FDB8 | 0x829DECD0, 0x829E6C90, 0x83076450 | UE3 OSL, Net-Z |
| XNetXnAddrToInAddr | 0x82E9FDD8 | 0x829E44E0, 0x830B1628 | UE3 OSL, Net-Z |
| XNetQosListen | 0x82E9FE60 | 0x829E1830, 0x829E4DE8, 0x829E6B38, 0x82C2DD50, 0x8307AE98 | UE3 OSL, game, Net-Z |
| XNetSetSystemLinkPort | 0x83363A28 | 0x83076110 | Net-Z XNet init |
| XNetStartup | (direct) | 0x82E9FCC0; logged by 0x8274AA58 (UE3) and 0x83076110 (Net-Z) | both |

### 1.3 Quazal Net-Z and Rendez-Vous are linked in [V]

UTF-16 strings in `.rdata`:

- `Quazal Net-Z` at `0x823042D7` (code ref `0x830CFED4`) and `Quazal
  Rendez-Vous` at `0x82306209` (ref `0x830E25A4`).
- PRUDP: `prudp:/`, `prudps:/` at `0x822F94A5`–`0x822F94E1`, and
  `prudps:/address=%s;port=%d;stream=%d;sid=%d;PID=%d;CID=1;type=%d` at
  `0x82305709`. The sliding-window, `FLAG_RELIABLE` and `FLAG_ACK` strings sit
  at `0x822FF8C9`–`0x822FF9AF`.
- Net-Z peer session: `JobJoinSession::*`, `JobConnectStation::*`,
  `JobProcessJoinRequest::*`, `ChangeMasterStation`, `StMigrateSession`
  (`0x822FB027`–`0x82303841`). Join needs XNet keys: "Received URL is missing
  XNKEY …" at `0x822FCC17` and "missing XNKID" at `0x822FCDA1`.
- Rendez-Vous: `JobBackEndServicesLogin::*`, `KerberosAuthentication::*`, and
  "Network: set RC4 encryption engine for Rendez-Vous Secure streams" at
  `0x822FD681`.
- LSP login: `JobLSPLogin::QueryLSPAddress`, `WaitForQueryLSPAddress`,
  `SecureConnect`, `StepXNetGetConnectStatus`, `BackEndServicesLogin` and
  `RVLogin`, plus `DummyLSP@quazal.com` and `DummyLSPclient.quazal.com`. One
  error string reads: "The LSP Security Gateway hosted by Quazal is
  unreachable. Check your title ID (xex.xml), service ID and your port. Then
  contact hosting support…" (`0x82306E19`, ref `0x830E9148`).
- Log categories `GameDevQuazal`, `GameDevOnline` (FNames at `0x8213A26D`) and
  ini suppressions `WooDevQuazal`, `WooDevOnline`.
- `0x8274A8E8` and friends use UE3's own `UTcpNetDriver`/`UIpNetDriverXe`.
  Nothing shows that UE3 actor replication carries match state. **[I]**

### 1.4 The game logs in to Rendez-Vous through an LSP [V]

- `0x82C24958` reads `[Quazal]`. It then calls `0x830DD880`, `0x830E84A0` and
  finally `0x830E87E8`, with `r7 = 0x545107FD` (`lis r7,0x5451` / `ori
  r7,r7,0x7fd` at `0x82C24B30`/`0x82C24B44`). The value has the THQ title-ID
  prefix `0x5451`, so it is the LSP service ID **[I]**.
  `0x82C24958` itself has no direct caller. Its pointer sits in the table at
  `0x82242EC0`, so it is a virtual method.
- The job steps chain through member-function pointers:
  `0x830E9328` QueryLSPAddress → `0x830E9240` WaitForQueryLSPAddress →
  `0x830E9090` SecureConnect → `0x830E8F68` StepXNetGetConnectStatus →
  `0x830E8DE8` BackEndServicesLogin → `0x830E8B18` RVLogin (string refs at
  `0x830E94C0`, `0x830E92D0`, `0x830E9214`, `0x830E905C`, `0x830E8E5C`,
  `0x830E8C98`; step pointers stored at `0x830E92D4`, `0x830E9210`,
  `0x830E9060`, `0x830E8E58`).
- `0x830E8FF4` calls `XNetServerToInAddr(ina, serviceId=[this+0x184], &out)`.
  Any non-zero return goes to the error path at `0x830E9000`, error
  `0x80010001`. On success, `0x830E9044` calls `XNetConnect(out)`.
- Strong inference: entering the online menus starts this login, and the
  online flow needs THQ/Quazal's LSP-hosted Rendez-Vous server. What RV
  provides beyond login is unconfirmed. The DDL shows at least one RV protocol,
  `GameDataLogProtocol` (`LogOnlineMatchResult(winnerID, …)` and
  `LogUniqueData(principalID, …)`), which is server-side match telemetry.
  **This is the main open question** (§5).

### 1.5 The game's own Net-Z data model [V]

DDL class and field names in `.data` at `0x83541610`–`0x83546010`, namespace
`GameWrestlingXBox360`:

| DDL object | Fields / methods | Meaning [I] |
|---|---|---|
| `SyncInputHandler` (SyncSim `SyncObject`) | `mJoypadAxes{mLSAxisX,mLSAxisY,mRSAxisX,mRSAxisY:int16}`, `mButtonBits{mBits}` | per-tick controller input for lockstep |
| `DOGameInfo` (RootDO) | `DSGameInfo{m_iNumPlayers, qlist<DSPlayerInfo>}`, `DSSessionNonce`, `DSMatchOptions{m_iRanked, m_iMatchType, m_iMatchStyle, m_iArena, m_iCustomContent, m_iPrivateSlots}`; RMCs `RegisterPlayerInfo`, `KickPlayer`, `PlayerSelectCharacter`, `UpdateScoreboard`, `SetArbitration`, `Heartbeat` | lobby, character select, results |
| `DOOnlineCapInfo` | `RequestPackageInfo`, `ReceivePackageInfo`, `StartSendingData`, `ReceiveData`, `OnDownloadFailed` | peer-to-peer transfer of created superstars (CAP) |
| `VoiceChannel` / `VoiceChatExt` | `HandleVoicePacket`, `AudioStreamFormat`, `VoicePacket{m_byTalkerID, m_bufVoiceData}` | voice over Net-Z |
| `GameDataLogProtocol` | `LogOnlineMatchResult`, `LogFirstTimeUseData` … | RV telemetry |

SyncSim library strings: `SyncSimAttributes`, `SimStation`, `SessionClock`,
`SyncSimReplayLog`, `SimulationChecksum`, `StationChecksum`,
`UserChecksum0`–`UserChecksum4`, `m_uiInputLatency`/`m_uiOutputLatency`
(`0x822FC88C`–`0x82305970`, `0x835C5789`–`0x835C5BBE`).

- `0x82A617D8` brings up SyncSim. It is gated on the global `0x8374058C`,
  needs a player count `[0x83740594]` in 1..4, and calls `0x830D9C00` with a
  20000 (`0x4E20`) argument at `0x82A619CC`. Its callers are `0x82BBBAE0` and
  `0x82BC1928`.
- **Built-in determinism tooling.** The command-line switches `NETZRECORD` and
  `NETZPLAYBACK` (UTF-16 at `0x82001B34`/`0x82001B4C`) are parsed at
  `0x82414578`–`0x824145AC` into `0x8374057C`/`0x83740580`. `0x82A617D8` uses
  them to pick `0x82A61420` (record) or `0x82A61638` (playback) of a SyncSim
  replay log. A separate `RECORDINPUT` switch exists at `0x82418910`.

Net-Z's socket factory at `0x830C2420` selects one of three cases:
`(SOCK_DGRAM, UDP 17)` for case 0, `(SOCK_STREAM, TCP 6)` for case 1, and
`(type 1, protocol 0xFE = VDP)` for case 2 (`0x830C2434`–`0x830C2458`). The
type-1 pairing in case 2 is odd, since VDP is a datagram protocol. It has not
been checked further. One more string: "VDP shoud be used for voice
transmission for TCR compliance / See XBoxNetworking::UseVDP()" at
`0x8223EEB8`, ref `0x82C15B08`.

### 1.6 Xbox Live configuration: the embedded xlast [V]

The SPA (XDBF) lives at `0x83820000`. Its `XSRC` entry at `0x838B4E33` holds
the gzip-compressed xlast `54510866.original.gameconfig` (34,152 bytes
compressed, 491,946 bytes UTF-16 decompressed).

- `titleId="0x54510866"`, `multiplayer="true"`, `<Privileges />`.
- **Matchmaking:** one query, `FindDefault` (id 1, maxResults 25). It filters
  on `MatchType` (`0x0C`: Standard, TagTeam, SteelCage, Elimination, Extreme,
  TablesMatch, TLC), `MatchStyle` (`0x12`: 2FFA, 3FFA, 4FFA, 1v2, 1v3, 2v2) and
  `UserContent` (`0x00`). It returns `GAME_TYPE` (`0x800A`: RANKED or
  STANDARD), `GAME_MODE` and Arena/player-count properties.
- **Contexts:** `GAME_TYPE`, `UserContent`, `Friends_Only`, `Time_Limit`,
  `MatchType`, `MatchStyle`.
- **Leaderboards:** 22 `StatsView`s (General, Overall, Std1v1 … TLCOverall),
  all `arbitrated="true"`, with TrueSkill-style `GAMERMU`/`GAMERSIGMA`
  properties.
- **Presence:** `GenOnlineMatch`, `InOnlineLobby`.
- **Achievements:** 45 in total. 7 need Xbox LIVE matches (190 G): ids 134,
  143, 144, 145, 146, 147, 154.

UI strings (`Coalesced.int`): `QUICK MATCH`, `PLAYER MATCH` ("do not impact
leaderboards"), `RANKED MATCH`. `Online/olStrEng.txt` has "Ping Time", "You
must be signed into Xbox LIVE to continue." and "Your gamer profile settings
do not allow you to play multiplayer game sessions." **No locale file
contains "System Link" or "LAN".**

### 1.7 Live web-service paths are XDK boilerplate [V]

`.data` `0x835B1200`–`0x835B24AA` lists `/xmatch/xmatchhost2.ashx`,
`/xstats/statpost.ashx`, `/XArb/Register.ashx` and similar. These are the
XDK online library's Live endpoints, not THQ servers. **[I]**

## 2. How ReXGlue implements these imports today [V]

| Area | ReXGlue behaviour | Source |
|---|---|---|
| `REX_EXPORT_STUB` | logs "STUB" and leaves `r3` unchanged, so a NetDll call returns the wrapper's `r3 = 1` | `include/rex/hook.h:52-56,76-78` |
| Stubbed XNet | `XNetConnect`, `XNetCreateKey`, `XNetGetConnectStatus`, `XNetQosLookup`, `XNetRegisterKey`, `XNetServerToInAddr`, `XNetUnregisterInAddr`, `XNetUnregisterKey`, `getsockname`, `getsockopt`, `WSAGetOverlappedResult` | `src/kernel/xam/xam_net.cpp:1005,1033-1051,1101-1102` |
| `XNetLogonGetTitleID` | stub | `src/kernel/xam/xam_misc.cpp:246` |
| `XNetGetTitleXnAddr` | loopback IP, `abEnet` = 0xCC×6, returns `XNET_GET_XNADDR_STATIC` | `xam_net.cpp:438-460` |
| `XNetXnAddrToInAddr` / `InAddrToXnAddr` | return 1 (fail), nothing filled | `xam_net.cpp:481-491` |
| `XNetSetSystemLinkPort` | returns 1 | `xam_net.cpp:495-497` |
| `XNetGetEthernetLinkStatus` | 0 (no link) | `xam_net.cpp:508-510` |
| `XNetDnsLookup` | allocates an `XNDNS` with status = error and signals the event | `xam_net.cpp:512-526` |
| `XNetQosListen` | `X_ERROR_FUNCTION_FAILED` | `xam_net.cpp:560-563` |
| `WSARecvFrom` | always −1 ("we're not going to be receiving packets any time soon") | `xam_net.cpp:298-315` |
| `WSASendTo` | real `sendto`, buffers concatenated, overlapped not completed | `xam_net.cpp:319-354` |
| BSD sockets | real host sockets; VDP mapped to plain UDP | `src/system/xsocket.cpp:45-61` |
| Sign-in | `user_profile->signin_state()` is hard-coded to `1` (signed in locally, not Live) | `include/rex/system/xam/user_profile.h:221`, `xam_user.cpp:68-77` |
| `XamUserCheckPrivilege` | every privilege denied (`*out_value = 0`) | `xam_user.cpp:373-387` |
| `XamUserGetMembershipTier` | 6 ("Gold") | `xam_user.cpp:429-437` |
| `XamSessionCreateHandle` / `RefObjByHandle` | fixed `0xCAFEDEAD` / `0xDEADF00D` | `xam_user.cpp:715-726` |
| `XSession*` (XGI messages 0xB0010–0xB001F) | log the arguments and return success, but write no results, search hits, details or arbitration data | `src/kernel/xam/apps/xgi_app.cpp:96-306` |
| `XUserReadStats` (0xB0021) | success, nothing filled | `xgi_app.cpp:319-334` |
| XLiveBase | NAT reported open; `XOnlineGetServiceInfo` (0x58007) returns `0x80151802` | `src/kernel/xam/apps/xlivebase_app.cpp:33-51` |
| XMsg routing | `XMsgStartIORequest(Ex)` → `app_manager()->DispatchMessageAsync` | `src/kernel/xam/xam_msg.cpp:54-84` |

Consequences:

- The menus should stop at the Live sign-in or privilege check. The Xenia
  baseline saw networking go no further than `XNetStartup`/`WSAStartup`
  (`docs/research/xenia-baseline.md:70`). **[I]** for what the recomp does.
- If those checks were forced through, the LSP login would fail at
  `0x830E8FF4`, and hosting would fail at `XNetCreateKey`. **[I]**

## 3. Prior art

- **Xenia Canary netplay fork** (AdrianCassar), checked at `6dbaa1fe`
  (2026-09-10). <https://github.com/AdrianCassar/xenia-canary>. It implements
  real XNet, sessions and LSP support against a REST backend, "Xenia Web
  Services" (<https://github.com/AdrianCassar/Xenia-WebServices>).
  - `NetDll_XNetServerToInAddr` treats `server_addr` as a real IPv4 address and
    returns success (`src/xenia/kernel/xam/xam_net.cc:804-825`).
  - `XNetConnect` sleeps 150 ms and returns success; `XNetGetConnectStatus`
    returns `STATUS_CONNECTED` (`xam_net.cc:786-802`).
  - `XOnlineGetServiceInfo` fills LSP service info from the server
    (`apps/xlivebase_app.cc:789-814`, "Backwards compatible XLSP" at 631).
  - Size of the online layer: `XLiveAPI.cpp` 2,606 lines, `xsession.cc` 1,367,
    `xam_net.cc` 2,723, `xgi_app.cc` 837, `xlivebase_app.cc` 3,204.
  - Licence: Xenia BSD-3 (`LICENSE`). ReXGlue is BSD-style and Xenia-derived
    (`.tools/rexglue-source/LICENSE`), so porting the code is legally
    straightforward.
- **Netplay compatibility list**, wiki at `cf548f91` (2026-06-30).
  <https://github.com/AdrianCassar/xenia-canary/wiki/Netplay-Compatibility>.
  It counts 559 working games. **WWE All Stars `54510866` is not listed.**
  Other THQ titles are:
  - SvR 2010 (`54510844`) and SvR 2011 (`5451085D`): working on public servers.
  - WWE Legends of WrestleMania (`54510838`): tested locally, "Gameplay is
    slow-mo".
  - Red Faction: Armageddon: "Player match works, ranked does not".
  - Saints Row 2: "only the pre-game lobby works".

  No list entry says whether those titles use Quazal.
- **Sibling port wwe-svr2011-pc** (permission to reuse granted). It added
  "real sockets, VDP over UDP, GameSpy DNS to a relay, QoS" to the SDK, plus
  "P2P XSession over UDP, LAN plus relay" and "Lockstep". That game used
  GameSpy, not Quazal (`docs/research/svr2011-pc-port.md:71,336-341`). Its
  XNet, VDP and XSession-over-UDP work transfers. Its GameSpy server does not.
- **Quazal / PRUDP server code:**
  - PretendoNetwork `nex-go`, a PRUDP/NEX server library with a "Quazal
    compatibility mode": <https://github.com/PretendoNetwork/nex-go>.
  - `ihatecompvir/nex-protocols-go`, built for Rock Band 3's Rendez-Vous:
    <https://pkg.go.dev/github.com/ihatecompvir/nex-protocols-go>. It "may work
    with other Quazal Rendez-vous titles".

  Both show that RV login, secure connection and matchmaking have been
  reimplemented for other titles. None targets WWE All Stars.
- **Not applicable:**
  - Insignia is original-Xbox only.
  - XLink Kai and Stealth/RGH System Link need a System Link mode, which this
    game does not appear to have (§1.6).
- **History:** THQ ended online play about two years after the April 2011
  launch. Aubrey Sitterson: "WWE All-Stars came out two years ago, and as a
  result, we no longer support online play"
  (<https://www.justpushstart.com/2013/04/wwe-all-stars-online-servers-shut-off/>).

## 4. Feasibility by tier

Every tier except (c) has to clear the same three gates first:

1. Sign-in state 2 (Live) and multiplayer privileges.
2. A Rendez-Vous login that succeeds, or is bypassed.
3. Working XNet addressing and keys, so Net-Z can connect stations.

### (a) LAN / direct-IP play through the game's own online mode

- **Work:**
  - Port the netplay fork's XNet layer to ReXGlue: XNADDR from the real NIC,
    `XnAddrToInAddr` mapping, `Create/RegisterKey`, `Connect` and status, QoS,
    overlapped `WSARecvFrom`/`WSASendTo`, VDP framing.
  - Implement XSession create, search, join and details. Search can run over a
    LAN broadcast or a tiny local server.
  - Clear the RV login. Option 1: a minimal RV server built on nex-go (PRUDP,
    `Authentication.Login`, `SecureConnection.Register`, and stub replies for
    `GameDataLogProtocol`). Option 2: a host hook that completes `JobLSPLogin`
    as successful. A hook goes through ReXGlue SDK patches; the manifest's
    no-stub rule is about codegen, but this still needs an ADR.
- **Why it might work:** the game transport is Net-Z SyncSim, which already
  does lockstep, and only inputs cross the wire. Once stations connect,
  bandwidth and latency handling are the game's own. **[I]**
- **Effort:** about 4–8 weeks part-time.
  - XNet and XSession port: 2–3 weeks (code exists).
  - RV login: 1 week with a hook, 2–5 weeks with a server, depending on how
    many RV calls the flow makes.
  - Two-machine testing on top of both.
- **Risk:** medium-high.
  - Net-Z's connectivity tests (`JobJoinSession::InitiateConnectivityTests`,
    "Connectivity Test Failed" at `0x822F92C9`) and QoS probes need faithful
    replies.
  - Arbitration (`XSessionArbitrationRegister`) must return a sensible
    registrant list for Ranked. Player Match may avoid it.

### (b) Internet play through a server we host

- **Work:** everything in (a), plus:
  - A session directory (run Xenia Web Services, or reimplement its REST API).
  - The RV server reachable from the internet.
  - NAT handling. The netplay fork relies on UPnP or port forwarding, and its
    wiki has a page for it. A relay for peers that cannot connect directly
    would add a TURN-style forwarder.
- **Effort:** +2–4 weeks on top of (a), plus ongoing hosting and moderation.
  Leaderboards could be emulated server-side from `XSessionWriteStats`, but
  the Live arbitration and TrueSkill semantics would be our own guesses.
- **Risk:** medium on top of (a). Mostly ops and NAT.

### (c) Emulator-level netplay over local multiplayer (no game online code)

- **Idea:** two copies of the recomp run the offline 2–4-player Exhibition.
  `XamInputGetState` is replaced by a lockstep or rollback input channel.
- **For:**
  - The 360 game was built so a match stays in sync from inputs alone (SyncSim
    plus checksums, §1.5).
  - The recomp runs the same code on both PCs.
  - `NETZRECORD`/`NETZPLAYBACK` and `RECORDINPUT` are ready-made determinism
    probes.
- **Against:**
  - SyncSim syncs only the match simulation, from an agreed start state.
    Whole-program lockstep also has to keep menus, 27 guest threads
    (`docs/research/xenia-baseline.md`), `KeQuerySystemTime`-seeded RNG,
    audio-driven timing and this repo's frame-pacing changes identical. The
    repo has already seen host-timing non-determinism
    (`docs/research/movement-judder.md:118`).
  - Rollback needs guest-memory plus GPU snapshots every frame. The image is
    0x18C0000 bytes plus heaps, so this is infeasible without major work.
- **Effort:** a 1–2 week spike to measure desync. Shipping lockstep-with-delay
  would take 4–8 weeks or more, if the spike passes.
- **Risk:** high. It also loses the online lobby, CAP sharing, voice and
  leaderboards.
- **Verdict:** a fallback, not the main path. A cheaper variant reuses the
  game's own SyncSim, which is tier (a).

### (d) What cannot be revived

- **The original online data.** THQ's Rendez-Vous servers and data are gone
  (§3), and so is any server-side state they held: telemetry from
  `GameDataLogProtocol`, and anything else RV kept, such as belt status,
  which is unverified. Live leaderboards and TrueSkill history are gone too.
  A replacement server can only start fresh boards.
- **Marketplace.** Purchase and entitlement flows cannot come back. DLC is
  already handled locally (ADR-0010 territory; the sibling port's
  `dlc_full_license`).
- **The Live system UI.** Friends, invites, gamercards and player review stay
  stubs (`xam_ui.cpp:612-674`) unless we draw our own UI.
- **Live achievement sync.** Local achievements work; the 7 online
  achievements would need a working online match.
- **Cross-play with real consoles.** It would need Live's security gateway and
  real XNet crypto.

## 5. Recommended first spike

**Goal:** find out how far the online menu gets once the Live gates are
forced, and exactly which Rendez-Vous steps it waits on. One person, about a
week. It needs a debug build and a dev branch, and the owner's approval
first: per `unattended-run-rules`, nothing ships.

1. In a scratch SDK patch, report `signin_state() = 2` and grant privileges
   in `XamUserCheckPrivilege` (`xam_user.cpp:373`). Log every `NetDll_*`,
   `XMsgStartIORequest` app/message pair, and `XamSession*` call with
   arguments.
2. Breakpoint or log these guest functions:
   - `0x82C24958`: Quazal config plus login entry.
   - `0x830E87E8`: login start (service ID `0x545107FD`).
   - `0x830E9328`: QueryLSPAddress. **How does it get the address?** Check
     `XNetDnsLookup` (callers `0x83099438`, `0x8274A7D8`), an XLiveBase
     message, or the `[Quazal] ServerAddress`.
   - `0x830E8F68`: `XNetServerToInAddr`/`XNetConnect`.
   - `0x830E8B18`: RVLogin.
   - `0x829DC8C8`: `XSessionSearch` path; `0x829DD010`: `XSessionCreate`;
     `0x829DECD0`: host key creation.
   - `0x83076110`: Net-Z XNet init; `0x82A5F000`: SyncSim bring-up.
3. Return success from `XNetServerToInAddr` (loopback) and point it at a
   PRUDP listener that only logs. This records the first RV packets
   (`Authentication.Login` or `LoginEx` with the guest account). They decide
   between "hook the job" and "write a minimal RV server".
4. Separately, try `NETZRECORD`/`NETZPLAYBACK` on the command line to see
   whether SyncSim record and playback work offline. If they do, they give a
   free determinism test for tiers (a) and (c).

Exit criteria:

- A list of the RV RMC calls made before the online lobby appears.
- Whether Player Match needs anything RV provides beyond login.
- A go/no-go for tier (a), which would amend ADR-0008.

## 6. Spike results (2026-10-05)

Tickets `.scratch/online-spike/issues/01`–`05`. Patch
`patches/rexglue-online-probe.patch` (retired 2026-10-07: `--wwe_online` replaced it, and
its call log is now `--wwe_net_trace`), cvar `--wwe_online_probe` (off, developer
only). Logs are under `analysis/session-online-*` and `analysis/session-two-*`.

### 6.1 How far the game gets [V]

- **Probe off:** Xbox LIVE shows "You must be signed into Xbox LIVE to continue."
- **Sign-in state 2 and privileges granted:** the game creates the login job,
  then shows SERVER NOT AVAILABLE. QueryLSPAddress is not `XNetDnsLookup`. It is
  `XTitleServerCreateEnumerator` (`0x833ABBA8`), which calls
  `XamCreateEnumeratorHandle` for XLiveBase message `0x58039`. The SDK refuses
  that handle.
- **The probe answers the LSP list with one server and XNet "connects":**
  "Logging In…". RVLogin binds UDP 1001 and sends a PRUDP v0 SYN to
  `<LSP>:32480`. The port comes from `[Quazal] ServerPort`; the address comes
  from Live, so the `devm` hostname is never used. The checksum uses the active
  `[Quazal] AccessKey`.
- **The listener answers the PRUDP handshake** (`scripts/online_listener.py
  --respond`): the first RMC is **`Authentication.Login("User")`** (0x0A/1),
  with the gamertag as the username. Unanswered, it times out into SERVER NOT
  AVAILABLE.
- The research's step map in §1.4 was off by one: `0x830E9328` is the
  JobLSPLogin constructor. The live order is `0x830E9240` (enumerate) →
  `0x830E9090` (wait) → `0x830E8F68` (ServerToInAddr, Connect) → `0x830E8DE8`
  (status) → `0x830E8B18` (RVLogin).

### 6.2 What Rendez-Vous has to answer [V] for the IDs, [I] for the order

A static map of every RMC request site (headers written by `0x83084A58`,
method IDs by `0x83079B90`) shows these calls:

| Phase | Call | Stand-in |
|---|---|---|
| Login | Authentication.Login (0x0A/1) | real reply: PID, Kerberos ticket, secure URL (an empty URL makes the game use auth port+1) |
| Login failed with InvalidUsername | AccountManagement 0x19 | not needed if every name is accepted |
| Secure connect | PRUDPS Kerberos connect | real |
| After connect | SecureConnection.Register or RegisterEx (0x0B/1 or 4) | real: CID, public URL |
| Secure stream to a new PID | Authentication.RequestTicket (0x0A/3), SecureConnection.RequestConnectionData (0x0B/2) | cheap; needed only if Net-Z peers use secure streams |
| Any time | GameDataLog (0x68/1–4) | empty success; never drop, the queue waits on it |

There's no RV matchmaking, friends, NAT traversal or persistent store, and
`LogOnlineMatchResult` is never sent. **Player Match needs nothing from RV
beyond login** (medium-high confidence).

### 6.3 SDK gaps the network port has to close [V]

The probe works around each of these behind its cvar:
- `XamCreateEnumeratorHandle` refuses the LSP list.
- `XNetServerToInAddr`, `XNetConnect` and `XNetGetConnectStatus` are stubs.
- `XSocket::SendTo` byte-swaps the destination: 127.0.0.1:32480 goes out as
  1.0.0.127:57470.
- `RecvFrom` swaps the source port.
- `WSARecvFrom` always fails, and its entry lacks `lpFromlen`.
- `WSAGetOverlappedResult` is a stub that returns TRUE, so Net-Z's receive loop
  spins about 12 million times a second.

The port should fix these for real, not behind a cvar. They are in addition to
the XNet key, XSession and QoS work in §4(a).

### 6.4 Two instances and determinism

- **Two instances [V]:** each copy needs its own `--user_data_root`. There's no
  instance lock; audio and cache are shared, and an unfocused copy keeps
  running. Quazal moves the second copy off port 1001 by itself. The clash is
  identity: every copy has XUID `0xB13EBABEBABEBABE` and the XNADDR/MAC
  127.0.0.1/`CC×6`. A per-install random XUID and MAC fixes it (ADR-0021 plans
  one).
- **Determinism, not tested [V]:** SyncSim's gate `0x8374053C` is set only by a
  Net-Z host/join (`0x82A5DD50`), so `-NETZRECORD` (passed with `--cl`) records
  nothing offline. The sync test becomes the first milestone of the post-v1
  build. It needs `--allow_game_relative_writes=true`, because the replay goes
  to `GAME:\Station<n>.log`.
- **No single-console shortcut [V]:** each console adds exactly one player to
  the session: `0x82BEEA48` builds one record, for the online manager's single
  user at +0x210. The lobby starts only when every slot of a 2-, 3- or 4-player
  match holds a ready player (`0x82B6E348`, slot table `{0,2,3,4,8,16}`).
  `0x82A5DC30` refuses a count of 1 or less. So the sync test needs two
  instances, and with them the XSession join path, not just hosting. Extra
  local users in the SDK would not help.

### 6.5 Revised effort

| Part | Before (§4a) | After the spike |
|---|---|---|
| Rendez-Vous stand-in | 1 week (hook) to 2–5 weeks (server) | **1–2 weeks** on nex-go: PRUDP v0, Login/ticket, secure connect, Register, GameDataLog stubs. The LSP list is solved (a few lines in XAM) |
| SDK sockets and XNet | 2–3 weeks with XSession | unchanged, plus the socket fixes in §6.3 (days) and a per-install identity |
| Determinism check | inside the spike | first post-v1 milestone; a desync blocks everything else (ADR-0021). **Passed 2026-10-08 (§6.7)** |
| **Tier (a) total** | 4–8 weeks part-time | **about 4–6 weeks part-time**, plus an unknown cost if SyncSim desyncs on PC |

### 6.6 What Net-Z needed from the SDK (online play ticket 07, 2026-10-07) [V]

From the two-copy runs on one PC (`analysis/session-online07*`), with `--wwe_online`:
- **Hosting** sets contexts `0x800B`/`0x800A` and three properties, creates the session
  (flags `0x82F`), joins its user locally and calls `XNetQosListen` with 0x28 bytes of
  `LISTEN_SET_DATA`.
- **Searching** calls `XSessionSearchEx` and then `XNetQosLookup` on the results; the
  host's QoS data has to come back with `DATA_RECEIVED`.
- **Joining** binds a UDP socket on port 9100 and a VDP socket (protocol `0xFE`) on port
  1000, calls `getsockname` on both, `XNetRegisterKey` with the session's XNKID and
  `XNetXnAddrToInAddr` on the host's XNADDR. A failure of either of the last two shows
  "MATCH UNAVAILABLE". With them answered (the XNADDR's own address; the key only
  remembered), the copies connect directly, both lobbies show 2/2, character select and
  the arena follow, and the 1v1 starts.
- **Net-Z peers don't use secure streams**: the join adds no `RequestTicket` or
  `RequestConnectionData` call to Rendez-Vous (§6.2's open risk is closed).
- Closing one copy mid-match shows "CONNECTION LOST!" on the other, which deletes its
  session on the server.
- Every SDK piece involved (keys, XnAddr/InAddr, QoS, `getsockname`, binding to
  `--wwe_online_ip`) is in `patches/rexglue-online.patch`, after the Xenia netplay fork.

### 6.7 Sync test verdict (online play ticket 09, 2026-10-08) [V]

**SyncSim stays in sync on PC.** Two copies on one PC played owner-driven 1v1 Player
Matches with `--cl=-NETZRECORD`. `scripts/syncsim_log.py compare` checked the two stations'
`Station<n>.log` step by step (format: `docs/research/syncsim-replay-log.md`):

| Build | Match | Steps | Result |
|---|---|---|---|
| baseline (x86-64-v2) | match1 | 7133 | SimulationChecksum, UserChecksum0-4 equal at every step |
| baseline (x86-64-v2) | match2, other wrestlers and arena | 3783 | equal at every step |
| AVX2 (x86-64-v3 -O2, `-ffp-contract=off`; from its `CMakeCache.txt`) | avx2 | 4355 | equal at every step |

`m_uiDivergingStep` is 0 in every header and nothing showed on screen. Recordings are under
`analysis/sync-test-*`. Limits:
- Both copies ran on one CPU with the same exe. Two different PCs (ticket 10) are the next
  test, and the two builds were never mixed in one match.
- The game builds its "SyncSim Diverge" message and frees it, so a live desync never
  reaches the runtime log. The header's `m_uiDivergingStep` is the record.
- `-NETZPLAYBACK` was dropped. It needs a hosted session and compares nothing against the
  recording.

## Open questions

Closed by the spike (§6):
- *Is the RV login on the critical path for every online mode?* Yes. Xbox LIVE
  runs the login before any mode menu, and a failed login shows SERVER NOT
  AVAILABLE.
- *Where does QueryLSPAddress get the address, and does the `devm` hostname
  matter?* From the Live LSP list (`XTitleServerCreateEnumerator`). The
  hostname is never used; the port and access key come from `[Quazal]`.

Still open:
- Does the online UI (`UI_ONLINE` package, `online_Lobby.swf`) have a hidden
  System Link entry? The localization says no, and the spike never got past the
  login to look.
- Is the sim deterministic across PCs with this repo's frame pacing? This needs
  a hosted Net-Z session (§6.4).
- ~~Do Net-Z peer links use secure streams?~~ No (§6.6).
