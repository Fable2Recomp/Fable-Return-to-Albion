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
    public void TestIsHandDummyAllowsGrippableItemsWithIdentityGrip()
    {
        // Handheld items often have identity grip matrices in Fable 2 because the skeleton dummy
        // already defines the natural hand grasp position. When child model path is known and is not
        // furniture (or isFurniture is explicitly false), items must return false (allowed to attach to hand bone).
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "ESA_Mug_V2.mdl")).IsFalse();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "Art/Environment/Shared assets/Props/dotXSI/ESA_Mug_V2/ESA_Mug_V2.mdl")).IsFalse();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "Tankard.mdl")).IsFalse();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "OB_SpiritBottle.mdl")).IsFalse();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Left", Transform3D.Identity, "ESA_Bowl.mdl")).IsFalse();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "Tool_Shovel.mdl")).IsFalse();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "Torch.mdl")).IsFalse();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "Food_Apple.mdl")).IsFalse();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "SH_LongSword_Spartan.mdl")).IsFalse();

        // When isFurniture is explicitly provided:
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, null, isFurniture: false)).IsFalse();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, null, isFurniture: true)).IsTrue();

        // Furniture on hands must return true (grounded on floor, rejected from hand bone)
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "ESA_F_Chair_Wooden_UltraWorn.mdl")).IsTrue();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Left", Transform3D.Identity, "Bench_Park.mdl")).IsTrue();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "Pub_Table_Round.mdl")).IsTrue();
        AssertBool(Engine_EntityAttachments.IsHandDummyWithoutGrip("Character.Carry.Hand.Right", Transform3D.Identity, "ESA_F_Stool_Wooden.mdl")).IsTrue();
    }

    [TestCase]
    public void TestIsFurnitureModelPathIdentifiesFurnitureCorrectly()
    {
        // Furniture paths and prefixes
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Environment/Furniture/Chairs/ESA_F_Chair_Normal.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Environment/Shared assets/Furniture/dotXSI/ESA_F_Cupboard_UltraDecorative/ESA_F_Cupboard_UltraDecorative.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("ESA_F_Chair_Wooden_UltraWorn.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("ESA_F_Stool_Wooden.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("Pub_Table_Round.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("Bench_Park.mdl")).IsTrue();
        AssertBool(GameEntities.IsFurnitureModelPath("ESA_F_Bookcase_Normal.mdl")).IsTrue();

        // Non-furniture items must NEVER match furniture
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Weapons/Swords/SH_LongSword_Spartan.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath("Crossbow_Light_Rusty.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Items/Food/Food_Apple.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath("Art/Items/Dishes/ESA_Mug.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath("Tool_Shovel.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath("Food_Vegetable.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath("Stable_Door.mdl")).IsFalse();
        AssertBool(GameEntities.IsFurnitureModelPath(string.Empty)).IsFalse();
    }
}
