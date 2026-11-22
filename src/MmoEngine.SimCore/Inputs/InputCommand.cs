namespace MmoEngine.SimCore.Inputs;

/// <summary>
/// One step's input from one player, format version 1 (FR-018,
/// specs/015-shared-simulation-core/contracts/input-command-format.md): the sequence number, the client
/// step it was made for, the buttons held, the aim as binary angles (yaw 65,536 per turn; pitch in
/// [−16384, 16384], positive looking up) and the weapon slot. <see cref="InputCodec"/> encodes it as 17
/// little-endian bytes without checking any value; <c>InputValidator</c> checks the values.
/// </summary>
public readonly record struct InputCommand(
    uint Sequence,
    uint ClientStep,
    Buttons Buttons,
    ushort AimYaw,
    short AimPitch,
    byte WeaponSlot);
