# BeltTwinUnity: Unity 3D twin and operator HMI for BeltTwin

This is the visualisation part of **[BeltTwin](https://github.com/rohithrvarun-vr/BeltTwin)**, a real-time digital twin of an industrial conveyor: TwinCAT 3 PLC → OPC UA → Node-RED → MQTT → Unity. The PLC, the data pipeline, the fault-detection work and all measured results are in the main repository.

![Unity twin, running](https://github.com/rohithrvarun-vr/BeltTwin/raw/master/docs/unity_running.png)

## What it does

- **Live twin.** Subscribes to `conveyor2/telemetry` over MQTT. Boxes move with the PLC's belt position, and the belt colour follows the PLC state. MQTT reconnects automatically.
- **Operator HMI in the ISA-101 style:** grey by default, colour only for abnormal states.
  - Process-value bars with warning and alarm marks. These are display limits, not detector thresholds.
  - A PLC fault banner.
  - On-demand trends with MQTT and latency diagnostics.
  - Operator controls kept separate from the fault-injection panel, which is for testing only.
- **Factory hall,** built at runtime (`FactoryEnvironment.cs`): floor markings, conveyor frame and drive, a control cabinet whose stack light follows the PLC state, and a camera that frames the belt between the HMI panels. Boxes enter and leave off-screen.
- **Fault-prediction panel.** Shows events from the Phase 1 live detector on `conveyor2/detection`. That model was trained on an earlier sensor model, so on current data its confidence is low.

![Unity twin, after a slip fault](https://github.com/rohithrvarun-vr/BeltTwin/raw/master/docs/unity_fault.png)

## Scripts

| File | Purpose |
|---|---|
| `Assets/BeltTwinClient.cs` | MQTT client (MQTTnet), commands, latency measurement, HMI |
| `Assets/BeltVisual.cs` | Belt and box motion from `rPosition`; belt extended so boxes wrap off-screen |
| `Assets/FactoryEnvironment.cs` | Procedural factory hall, stack light, camera framing (creates itself on Play) |

## Running it

Unity 2022.3 (built with 2022.3.61f1). It needs an MQTT broker on `localhost:1883` and the BeltTwin pipeline publishing telemetry. Without the pipeline, the HMI shows "NO DATA - CHECK PIPELINE". Open the project, then open `SampleScene` and press Play.
