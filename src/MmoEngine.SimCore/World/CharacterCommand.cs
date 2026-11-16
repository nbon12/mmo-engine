using MmoEngine.SimCore.Inputs;

namespace MmoEngine.SimCore.World;

/// <summary>
/// One character's input for one world step: the character's index in the world's character list
/// (ascending character ID) and its decoded, not yet validated, command. A step takes at most one per
/// character, in ascending index.
/// </summary>
public readonly record struct CharacterCommand(int CharacterIndex, InputCommand Command);
