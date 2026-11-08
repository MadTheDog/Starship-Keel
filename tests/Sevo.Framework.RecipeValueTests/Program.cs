using Game;
using Sevo.Framework.Runtime;
using VoxelEngine;

var output = new CraftingElement { itemType = WorldItemType.craftingParts, id = 10, quantity = 2f };
var input = new CraftingElement { itemType = WorldItemType.ingot, id = 20, quantity = 1f, contextualDataId = 7 };
var valid = new RecipeDefinition(CraftingLevel.craftingBench, 5f, output, input);
if (valid.Inputs[0].contextualDataId != 7) throw new Exception("Contextual ingredient was changed.");
void Reject(CraftingElement invalid, string label)
{
    try { _ = new RecipeDefinition(CraftingLevel.craftingBench, 5f, invalid, input); }
    catch (ArgumentException) { return; }
    throw new Exception(label);
}
var contextualOutput = output;
contextualOutput.contextualDataId = 7;
Reject(contextualOutput, "Native craft consumers would discard this contextual output ID.");
var flaggedOutput = output;
flaggedOutput.itemFlags = (ItemInstanceFlags)1;
Reject(flaggedOutput, "Native craft consumers would discard these output flags.");
Console.WriteLine("Recipe output identity and ingredient validation passed");
