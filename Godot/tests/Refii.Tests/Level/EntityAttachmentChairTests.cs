namespace Refii.Tests.Level;

using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Entities;
using Refii.Game;

/// <summary>
/// Verifies the furniture attachment classification and hand-attachment bypass logic.
/// Prevents chairs, benches, stools, and world props from being lifted into the air like mugs
/// when NPCs sit down, ensuring they remain grounded on the floor (stated).
/// </summary>
[TestSuite]
public class EntityAttachmentChairTests
{
    [TestCase]
    public void TestIsHandDummyWithoutGripIdentifiesWorldPropsOnHands()
    {
        // Identity grip on hand attachment = world prop / furniture being dragged on floor
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Left", Transform3D.Identity)).IsTrue();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity)).IsTrue();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("character.carry.hand.left", Transform3D.Identity)).IsTrue();

        // Real carried handheld objects have author-defined grip offsets (non-identity)
        Transform3D swordGrip = new Transform3D(Basis.Identity, new Vector3(0f, -0.15f, 0.05f));
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", swordGrip)).IsFalse();

        Transform3D mugGrip = new Transform3D(Basis.Identity, new Vector3(0.02f, 0.04f, 0f));
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Left", mugGrip)).IsFalse();

        // Non-hand dummies (e.g. sheaths, backs, heads) are not hand pulls
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.SheathWeaponBack", Transform3D.Identity)).IsFalse();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Prop.Layout.Position", Transform3D.Identity)).IsFalse();
    }

    [TestCase]
    public void TestIsFurnitureModelPathIdentifiesChairsTablesAndBenches()
    {
        // Ultra-worn tavern chair observed in The Golden Cock
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Environment/Furniture/Chairs/dotXSI/ESA_F_Chair_Wooden_UltraWorn/ESA_F_Chair_Wooden_UltraWorn.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("ESA_F_Chair_Wooden_UltraWorn.mdl")).IsTrue();

        // Other furniture variants
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Environment/Furniture/Chairs/ESA_F_Chair_Normal.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Environment/Furniture/Stools/ESA_F_Stool_Wooden.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Environment/Furniture/Tables/Pub_Table_Round.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Environment/Furniture/Benches/Bench_Park.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("ESA_F_Bookcase_Normal.mdl")).IsTrue();

        // Non-furniture items must never match
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Weapons/Swords/SH_LongSword_Spartan.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath("Crossbow_Light_Rusty.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Items/Food/Food_Apple.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Items/Dishes/ESA_Mug.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath("Tool_Shovel.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath(string.Empty)).IsFalse();
    }
}
