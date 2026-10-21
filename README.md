# Unity Smart Relays sample (LightNet + Edgegap Relays)

A small Unity game where one player hosts and the others join with a lobby code. All traffic goes
through an [Edgegap relay](https://docs.edgegap.com/learn/distributed-relay), so nobody needs port
forwarding, and strict NAT or firewalls don't block anyone. It is built on
[LightNet](Assets/LightNet/README.md), a tiny raw-bytes networking library for Unity. This repo is a
fork of [Ben1138/lightnet-unity](https://github.com/Ben1138/lightnet-unity).

What's in it:

| Path | What it is |
|---|---|
| `Assets/LightNet/` | The LightNet library (upstream), plus `INetworkService` so it can run on other backends, and a few bug fixes. |
| `Assets/Edgegap/Relay/` | **The relay transport.** `RelayNetworkService` (Edgegap relay protocol, reliable and unreliable channels over UDP), `StartRelayHost` / `StartRelayClient` extensions for `NetworkComponent`, and `RelayLobbyClient`. Copy this folder into your own LightNet game. |
| `Assets/Edgegap/Sample/` | The demo: everyone moves a cube and chats. It has a host/join UI and command-line flags for automated tests. |
| `lobby-service/` | A minimal lobby service (Node 18+, no dependencies). It holds the relay token, creates a relay session per lobby and authorizes each joining player. |
| `tools/local-relay.js` | An offline emulator of the relay protocol, for transport tests without billing. It is not a substitute for a real relay test. |

> **Relay or dedicated server?** A relay only forwards packets. One player's game is the host and
> owns the game state, so the host can cheat, has zero latency while everyone else has two hops, and
> the match ends when the host quits. Pick relays when the game is designed host-client (co-op,
> friends lobbies) and a dedicated server is not wanted. For competitive games, use
> [Edgegap dedicated servers](https://docs.edgegap.com/learn/orchestration/deployments) instead.

## How it works

```
 Host game ──┐                                       ┌── Client game
             │ 1. POST /lobbies                      │ 3. POST /lobbies/CODE/join
             ▼                                       ▼
          ┌───────────────── lobby-service ─────────────────┐
          │ holds EDGEGAP_RELAY_TOKEN                        │
          │ 2. POST /v1/relays/sessions (host IP)           │
          │ 4. POST /v1/relays/sessions:authorize-user (IP) │
          └──────────────────────────────────────────────────┘
             │ relay ip, server port,                │ relay ip, client port,
             │ session token, host user token        │ session token, client user token
             ▼                                       ▼
 Host ── UDP ──► relay SERVER port   [Edgegap relay]   relay CLIENT port ◄── UDP ── Client
```

1. The host asks the lobby service for a lobby. The service creates a relay session with the host's
   public IP, waits until it is `ready`, and returns a 6-letter code with the relay address and tokens.
2. Each client sends the code. The service authorizes the client's public IP on that session and
   returns the relay's client port and that player's token.
3. Every game opens a UDP socket to the relay. Each one sends a relay PING every 500 ms with
   `[user token][session token]` until the relay reports `Valid`.
4. The client says Hello to the host through the relay. The host answers Welcome. LightNet then fires
   `OnClientConnected` on both sides, the same as with direct sockets.
5. When the host leaves, the game calls `DELETE /lobbies/CODE`, and the service deletes the relay
   session. Relay sessions bill while they are open. The service also expires lobbies after
   `LOBBY_TTL_MINUTES` (default 120) and deletes every open session when it stops.

The relay forwards unreliable UDP datagrams of up to 1200 bytes and adds up to 13 bytes of its own.
`RelayNetworkService` implements LightNet's two channels on top of that:

| Channel | Over the relay |
|---|---|
| `ENetChannel.Reliable` | Acknowledged, resent every 200 ms until acked, delivered in order. Messages up to 64 KB are split into 1184-byte fragments. |
| `ENetChannel.Unreliable` | Can drop, but never arrives out of order (older datagrams are discarded). Up to **1182 bytes** per message, with no fragmentation. |

A peer that sends nothing for 5 s is considered disconnected. Keepalives go out every 500 ms.

## Run it

Requirements: Unity **6000.0.54f1** (the version this was built and tested with), Node.js 18+, and an
Edgegap account.

### 1. Get a relay token

In the [Edgegap dashboard](https://app.edgegap.com), open **Relays** and create a relay profile, then
copy its token. Relay endpoints need this token. A regular API token gets `401`/`403` on
`/v1/relays/*`.

### 2. Start the lobby service

```bash
cd lobby-service
cp .env.example .env        # then put your token in .env: EDGEGAP_RELAY_TOKEN=...
npm start                   # listens on http://localhost:8080
```

Keep the token on the lobby service. Never put it in the game build: anyone who has it can open relay
sessions on your account.

When the lobby runs on your own machine, players on that machine and your LAN reach it from a private
address. In that case the service authorizes your public IP instead (looked up once through
api.ipify.org). Players on other networks need a lobby they can reach, see
[Deploy the lobby service](#deploy-the-lobby-service).

### 3. Play

Open the project in Unity, then open `Assets/Edgegap/Sample/RelaySample.unity` and press Play.
Alternatively, build with **Edgegap > Relay Sample > Build Windows** (also Linux and macOS), or run
this:

```bash
Unity -batchmode -quit -projectPath . -executeMethod LightNet.Edgegap.Editor.RelaySampleBuild.BuildWindows
```

Run two copies. In the first, press **Host**, and a lobby code appears. In the second, type the code
and press **Join with code**. Move with WASD or the arrow keys, and type in the chat box to send
reliable messages. **Leave** on the host closes the lobby and its relay session.

### Command line (automated tests)

```bash
# host, write the code to a file, quit when everyone left
RelaySample.exe -batchmode -nographics -lobby http://localhost:8080 -host -codeFile code.txt -quitWhenEmpty -quitAfter 300
# two clients: reliable echo test (60 messages, 64 B to 20 KB), and wait to see all 3 cubes
RelaySample.exe -batchmode -nographics -lobby http://localhost:8080 -join CODE -e2e -expectPlayers 3
```

| Flag | Meaning |
|---|---|
| `-lobby URL` | Lobby service URL (default `http://localhost:8080`). |
| `-host` / `-join CODE` | Host, or join, right away. |
| `-maxPlayers N` | Players per lobby, host included (default 4). |
| `-relayAddress A -relayPort P -sessionToken S -userToken U` | Skip the lobby and use tokens from the API or `edgegap_create_relay_session`. Use the server port to host, or add `-client` and the client port to join. |
| `-e2e` | Client: sends 60 reliable messages to the host. The host echoes them back, and the client checks order and content, logs `E2E PASS` and exits with code 0 (`E2E FAIL` exits with 1). |
| `-expectPlayers N` | With `-e2e`, also wait until the cubes of N players in total are visible. This checks that the host forwards client states. |
| `-quitWhenEmpty` | Host: once every client has left, close the lobby and quit. |
| `-quitAfter SECONDS` | Quit after this long. |
| `-loss PERCENT` | Drop this share of outgoing datagrams, to exercise the reliable channel. |

## Use it in your own LightNet game

Copy `Assets/LightNet` and `Assets/Edgegap/Relay` into your project. Then replace `StartServer` and
`StartClient` with the relay versions. Your `NetworkData` types and event handlers stay as they are.

```csharp
using LightNet;
using LightNet.Edgegap;

// host: RelayTicket from your lobby / matchmaker (here: the sample lobby service)
StartCoroutine(new RelayLobbyClient(lobbyUrl).Host(4, ticket =>
{
    RelayNetworkService relay = net.StartRelayHost(ticket.ToEndpoint(), maxClients: 3);
    Debug.Log("Share this code: " + ticket.code);
}));

// client
StartCoroutine(new RelayLobbyClient(lobbyUrl).Join(code, ticket =>
{
    net.StartRelayClient(ticket.ToEndpoint());
}));

// everything else is plain LightNet
net.OnClientConnected += (s, e) => Debug.Log("connected " + e.Handle.GetInternalHandle());
net.BroadcastNetworkData(ENetChannel.Unreliable, myState);
```

If you already have a backend (your own lobby, a matchmaker, Nakama, PlayFab and so on), have it make
the same two Edgegap calls as `lobby-service/server.js`. It sends the host's endpoint (relay IP and
**server** port) and each client's endpoint (relay IP and **client** port), with the session token and
that player's user token. Build a `RelayEndpoint` from those values.

Clients only talk to the host. The relay does not forward client-to-client traffic, so the host has
to forward anything other clients need to see. `RelaySample` shows how: player states and chat go to
the host, which sends them on.

## Deploy the lobby service

The service is one Node file with a Dockerfile, so it runs anywhere that can serve HTTP. It must see
each player's real public IP, because the relay is chosen by player location and only accepts
authorized IPs. Behind a reverse proxy or load balancer, set `TRUST_PROXY=1` so the service uses
`X-Forwarded-For`. Keep it to one instance, since lobbies are kept in memory. A production lobby
also needs player authentication, rate limits and persistent storage.

```bash
docker build -t relay-lobby lobby-service
docker run -p 8080:8080 -e EDGEGAP_RELAY_TOKEN=... relay-lobby
```

| Endpoint | |
|---|---|
| `POST /lobbies` `{"max_players":4}` | 201: `code`, `host_secret`, `session_id`, `relay_ip`, `relay_host`, `relay_port` (server port), `session_token`, `user_token` |
| `POST /lobbies/{code}/join` | 200: the same fields for a client (`relay_port` is the client port). 404 for an unknown code, 409 when the lobby is full. |
| `GET /lobbies/{code}` | Player count, with no tokens. |
| `DELETE /lobbies/{code}` with header `X-Host-Secret` | 204: closes the lobby and deletes the relay session. |
| `GET /health` | `{"ok":true}` |

## Test the transport offline

```bash
node tools/local-relay.js --loss 15
RelaySample.exe -batchmode -nographics -relayAddress 127.0.0.1 -relayPort 41000 -sessionToken 1000 -userToken 1 -quitWhenEmpty
RelaySample.exe -batchmode -nographics -client -relayAddress 127.0.0.1 -relayPort 41001 -sessionToken 1000 -userToken 2 -e2e -expectPlayers 3
RelaySample.exe -batchmode -nographics -client -relayAddress 127.0.0.1 -relayPort 41001 -sessionToken 1000 -userToken 3 -e2e -expectPlayers 3
```

The emulator speaks the documented relay protocol and can drop packets on purpose. It does no
location selection and no NAT traversal, so still run the real test through the lobby service.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Lobby service: `401`/`403` from `/v1/relays/sessions` | The token is not a relay profile token. Create one under **Relays** in the dashboard. |
| `Relay state: Invalid` in the game log | The session or user token is wrong, or this player's public IP is not authorized on the session. Each player must use its own `user_token`. The host uses the **server** port and clients use the **client** port. |
| `No answer from the relay ... after 15 s` | The session was deleted, the port is wrong, or outbound UDP is blocked. |
| `The host did not answer within 15 s` | The client is valid on the relay, but the host is not connected (it left or never started), or the host is full. |
| Unreliable message rejected (`exceeds the relay limit`) | Keep unreliable messages at or below 1182 bytes, or send them reliable. |
| Lobby works locally but friends can't join | `localhost` only works on your machine. Deploy the lobby service and point `-lobby` at it. |

Relay sessions bill while they are open. Close lobbies when the match ends, and check the dashboard,
`GET /v1/relays/sessions` or the Edgegap MCP `edgegap_list_relay_sessions` for leftover sessions.

## License

Apache 2.0 (see [LICENSE](LICENSE)). LightNet is by Ben1138; the Edgegap relay transport, sample and
lobby service are released under the same license.
