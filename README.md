# BD Viper LT – ASTM Emulator

A Windows desktop app (WPF, .NET 10) that emulates a **BD Viper LT** reporting **BD Onclarity HPV** results to a LIS using ASTM:

- **Low level:** ASTM E1381 / CLSI LIS1-A (ENQ/ACK/NAK/EOT, STX‑framed, mod‑8 frame numbers, checksums, ETB continuation frames, retries, timeouts, line contention).
- **High level:** ASTM E1394 / CLSI LIS2-A2 records (H, P, O, C, R, Q, L).
- **Transports:** TCP client, TCP server, RS‑232 serial.

> **Important:** this is not built from BD's LIS interface specification for the Viper LT. The record layout follows the LIS2‑A2 standard and the Onclarity reporting channels. Before you rely on it for validation, compare the field positions, test codes and result values with the BD Viper LT LIS interface manual. You can change all of them in the UI, and the Message editor tab lets you send any text you like.

## Build & run

```bash
dotnet build ViperAstmEmulator.sln
```

```bash
dotnet run --project src/ViperAstmEmulator
```

Command line flags: `--connect` (connect on start), `--send-all` (send all specimens once connected), `--auto-send`.

## Using it

A full user manual in Dutch is in [src/ViperAstmEmulator/Help/Handleiding.html](src/ViperAstmEmulator/Help/Handleiding.html). Open it in the app with **Help** or **F1**.

1. **Connection tab:** choose a mode (TCP client to the LIS, TCP server the LIS connects to, or serial) and click **Connect**.
2. **Specimens tab:** add specimens using a scenario (Random, Negative, HPV16/18/45 positive, other high-risk, multiple genotypes, control failure, indeterminate). Select a specimen to edit the result and Ct for each genotype.
3. **Send selected / Send all** sends the results. **Host query** sends a `Q` record. Orders the LIS sends back are added to the list.
4. **Message editor** lets you build a message, change it by hand (one record per line) and send exactly that text.
5. **Auto-send** creates and sends one random specimen per interval, for soak testing.
6. **Error injection** (live): send a bad checksum on the first frame (the LIS should NAK it), or NAK the first frame received from the LIS (the LIS should retransmit).

The **communication log** shows all traffic with visible control characters, for example `<STX>1H|\^&|||BD Viper LT^VLT0001^3.0|||||LIS||P|LIS2-A2|20261114163509<CR><ETX>E0<CR><LF>`.

Settings are saved to `%APPDATA%\ViperAstmEmulator\settings.json`, and unexpected errors go to `crash.log` in the same folder.

## Example message (one specimen, HPV 16 positive)

```
H|\^&|||BD Viper LT^VLT0001^3.0|||||LIS||P|LIS2-A2|20261114103000
P|1|P1|||Doe^Jane||19800101|F
O|1|HPV000042|R01^3|^^^HPV|R||||||||||Cervical|||||||20261114103000|||F
R|1|^^^HPV16|POS|||||F||OPERATOR|20261114080000|20261114103000|BD Viper LT^VLT0001
R|2|^^^HPV16^CT|27.3|||||F||OPERATOR|20261114080000|20261114103000|BD Viper LT^VLT0001
R|3|^^^HPV18|NEG|||||F||...
...  (HPV45, 31, 51, 52, 33/58, 35/39/68, 56/59/66)
R|n|^^^BC|POS|||||F||...
R|n|^^^HPV|POS|||||F||...          <- overall interpretation
L|1|N
```

Options: Ct as a separate record (`^^^CODE^CT`), as a result component (`POS^27.3`), or left out. You can also include or leave out demographics, rack^position, the BC control and the overall result. Messages can be one per specimen or batched, and trailing delimiters can be trimmed.

Overall interpretation: any genotype POS gives **POS**. Otherwise a failed beta-globin control gives **INV**, any indeterminate genotype gives **IND**, and anything else is **NEG**.

## LIS simulator (for testing without a LIS)

```bash
dotnet run --project tools/LisSimulator -- 5000
```

It listens on port 5000, ACKs everything, prints the received records and answers host queries with orders. Use `-- --connect host:port` to connect to the emulator when the emulator runs in TCP server mode.

## Layout

| Path | Contents |
|---|---|
| `src/ViperAstmEmulator.Core/Astm` | Framing (`AstmFramer`), link state machine (`AstmLink`), transports, reconnecting `AstmSession` |
| `src/ViperAstmEmulator.Core/Viper` | Onclarity targets, specimen model, scenarios, `ViperMessageBuilder`, order parser, settings |
| `src/ViperAstmEmulator` | WPF UI (MVVM) |
| `tools/LisSimulator` | Console LIS for testing |
| `tests/ViperAstmEmulator.Tests` | Framing, message and TCP loopback tests (NAK/retransmit, contention) |
