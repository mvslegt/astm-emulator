# Lab Interface Emulator – ASTM · HL7 v2 · FHIR

A Windows desktop app (WPF, .NET 10) for testing LIS interfaces with **BD Onclarity HPV** results. It has two tabs:

- **ASTM (BD Viper LT)** emulates a BD Viper LT reporting over ASTM (described below).
- **HL7 v2 / FHIR** sends and receives HL7 v2 messages over MLLP and FHIR R4 over HTTP (see [HL7 v2 / FHIR](#hl7-v2--fhir)).

The ASTM tab covers:

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

## HL7 v2 / FHIR

**HL7 v2 (MLLP over TCP, client or server, default port 2575)**

- Sends `ORU^R01`, `OUL^R22`, `ORM^O01`, `OML^O21`, `ADT^A01/A04/A08` and `QBP^Q11`, in versions 2.3.1–2.6. From 2.5 on, the specimen goes in an SPM segment; before 2.5 it goes in OBR-15, with CE instead of CWE.
- Waits for the ACK, matched by MSH-10 = MSA-2, with a timeout, and shows the result.
- Acknowledges messages from the LIS with AA, AE or AR, or not at all. This can be changed while connected.
- Adds orders received from the LIS (ORM, OML, RSP) to the specimen list.

**FHIR R4 (JSON over HTTP)**

- Sends a results transaction Bundle (Patient, Specimen, ServiceRequest, one Observation per channel and a DiagnosticReport), a FHIR message Bundle to `$process-message`, an order transaction, a standalone DiagnosticReport or a Patient. It can also search open ServiceRequests or fetch `metadata`. An optional bearer token can be set.
- Results use SNOMED CT for Positive, Negative and Indeterminate, v3 ObservationInterpretation codes, and Ct as a `component` in `{cycles}`. Code and identifier systems can be configured; the defaults are `example.org` placeholders.
- A built-in FHIR server, by default `http://localhost:8090/fhir/`, receives data from the LIS. It handles transaction and batch Bundles, `$process-message`, create, update and read of resources, search and `metadata`. It can also answer with 400 or 500 to test the LIS's error handling.

Both editors (**HL7 v2 message** and **FHIR request**) let you change a message before sending it. Command line flags: `--hl7-connect`, `--hl7-send-all`, `--fhir-server`. Settings are saved to `%APPDATA%\Hl7Emulator\settings.json`.

To test HL7 without a LIS, run the LIS simulator in HL7 mode. It ACKs everything and, with `--send-order`, sends one `OML^O21` order back:

```bash
dotnet run --project tools/LisSimulator -- --hl7 2575 --send-order
```

## Layout

| Path | Contents |
|---|---|
| `src/ViperAstmEmulator.Core/Astm` | Framing (`AstmFramer`), link state machine (`AstmLink`), transports, reconnecting `AstmSession` |
| `src/ViperAstmEmulator.Core/Viper` | Onclarity targets, specimen model, scenarios, `ViperMessageBuilder`, order parser, settings |
| `src/Hl7Emulator.Core/Hl7v2` | HL7 v2 parser, message factory and ACKs (`Hl7MessageFactory`), MLLP connection and session |
| `src/Hl7Emulator.Core/Fhir` | FHIR R4 resource and Bundle builder, HTTP client, built-in FHIR server |
| `src/ViperAstmEmulator` | WPF app: `Views/AstmView` (ASTM tab) and `Views/Hl7View` (HL7 v2 / FHIR tab) |
| `tools/LisSimulator` | Console LIS for testing (ASTM, or HL7 v2 with `--hl7`) |
| `tests/ViperAstmEmulator.Tests` | ASTM framing and loopback, HL7 v2 messages and MLLP/ACK loopback, FHIR bundles and HTTP round trip |
