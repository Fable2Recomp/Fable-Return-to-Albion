namespace Refii.Tests.Material;

using System.Collections.Generic;
using GdUnit4;
using Refii.Models;
using static GdUnit4.Assertions;

/// <summary>
/// Fable 3's shader selection against its own tables: every expected name below is the entry the
/// guest's index table and record table give for those inputs (standard dword_83292BD0 /
/// dword_8329BBD0 and dword_83292478 / dword_83292678, tree dword_8329C808 / dword_8329C4C0,
/// repeated dword_83291078 / dword_83290EE0), read from the binary.
/// </summary>
[TestSuite]
public class ClassicTokensFable3Tests
{
    private const string PS = "PSHADER";
    private const string VS = "VSHADER";

    private static string StandardPs(int ambient, int lights, bool mist = false, bool morph = false, bool sp = false,
        bool stsp = false, bool sh = false, bool fr = false, bool self = false, bool env = false) =>
        Engine_ClassicTokensFable3.Entry(PS, Engine_ClassicTokensFable3.Standard,
            Engine_ClassicTokensFable3.StandardPixelTokens(ambient, lights, mist, morph, sp, stsp, sh, fr, self, env));

    [TestCase]
    public void StandardPixelTakesTheSpecialisedShapes()
    {
        AssertThat(StandardPs(2, 1))
            .IsEqual("PSHADER_STANDARDMATERIAL_LGHTS1_MIST0_AMB2_CMRPH0_SP0_STSP0_SH0_FR0_SELF0_ENV0");
        AssertThat(StandardPs(2, 3, sp: true, fr: true))
            .IsEqual("PSHADER_STANDARDMATERIAL_LGHTS3_MIST0_AMB2_CMRPH0_SP1_STSP0_SH0_FR1_SELF0_ENV0");
        AssertThat(StandardPs(2, 2, sp: true, stsp: true, sh: true))
            .IsEqual("PSHADER_STANDARDMATERIAL_LGHTS2_MIST0_AMB2_CMRPH0_SP1_STSP1_SH1_FR0_SELF0_ENV0");
        AssertThat(StandardPs(3, 4, sp: true, stsp: true))
            .IsEqual("PSHADER_STANDARDMATERIAL_LGHTS4_MIST0_AMB3_CMRPH0_SP1_STSP1_SH0_FR0_SELF0_ENV0");
    }

    [TestCase]
    public void StandardPixelFallsToTheUberShader()
    {
        const string Any = "_CMRPH_ANY_SP_ANY_STSP_ANY_SH_ANY_FR_ANY_SELF_ANY_ENV_ANY";
        // A shape the bank did not compile keeps only the count and the ambient fixed.
        AssertThat(StandardPs(3, 1, sp: true)).IsEqual("PSHADER_STANDARDMATERIAL_LGHTS1_MIST_ANY_AMB3" + Any);
        AssertThat(StandardPs(2, 1, mist: true)).IsEqual("PSHADER_STANDARDMATERIAL_LGHTS1_MIST_ANY_AMB2" + Any);
        AssertThat(StandardPs(2, 2, env: true)).IsEqual("PSHADER_STANDARDMATERIAL_LGHTS2_MIST_ANY_AMB2" + Any);
        // No lights, or more than four, take the light-count uber shader.
        AssertThat(StandardPs(2, 5)).IsEqual("PSHADER_STANDARDMATERIAL_LGHTS_ANY_MIST_ANY_AMB2" + Any);
        AssertThat(StandardPs(2, 0)).IsEqual("PSHADER_STANDARDMATERIAL_LGHTS_ANY_MIST_ANY_AMB2" + Any);
        AssertThat(StandardPs(0, 1)).IsEqual("PSHADER_STANDARDMATERIAL_LGHTS_ANY_MIST_ANY_AMB0" + Any);
        // AMB1's record is 0: no shader.
        AssertThat(StandardPs(1, 1)).IsEqual(string.Empty);
    }

    [TestCase]
    public void StandardVertexNamesItsShapeWhereOneExists()
    {
        AssertThat(Engine_ClassicTokensFable3.Entry(VS, Engine_ClassicTokensFable3.Standard,
                Engine_ClassicTokensFable3.StandardVertexTokens(3, false, true)))
            .IsEqual("VSHADER_STANDARDMATERIAL_AMB3_COL0_SKN0_FIX0_SP1");
        AssertThat(Engine_ClassicTokensFable3.Entry(VS, Engine_ClassicTokensFable3.Standard,
                Engine_ClassicTokensFable3.StandardVertexTokens(2, true, true)))
            .IsEqual("VSHADER_STANDARDMATERIAL_AMB2_COL1_SKN0_FIX0_SP1");
        AssertThat(Engine_ClassicTokensFable3.StandardVertex(0, false, 1, true, false))
            .IsEqual("VSHADER_STANDARDMATERIAL_AMB0_COL0_SKN1_FIX1_SP0");
        AssertThat(Engine_ClassicTokensFable3.StandardVertex(3, false, 1, false, false)).IsEqual(string.Empty);
        AssertThat(Engine_ClassicTokensFable3.StandardVertex(2, false, 0, true, false)).IsEqual(string.Empty);
    }

    [TestCase]
    public void TreeLightsTheTrunkPerPixelAndTheCanopyPerVertex()
    {
        string Ps(int ambient, int lights, bool mist, bool sp, bool canopy) => Engine_ClassicTokensFable3.Entry(PS,
            Engine_ClassicTokensFable3.Tree, Engine_ClassicTokensFable3.TreePixelTokens(ambient, lights, mist, sp, canopy));
        string Vs(int ambient, int lights, bool sp, bool canopy) => Engine_ClassicTokensFable3.Entry(VS,
            Engine_ClassicTokensFable3.Tree, Engine_ClassicTokensFable3.TreeVertexTokens(ambient, lights, sp, canopy));

        AssertThat(Ps(2, 1, false, true, false)).IsEqual("PSHADER_TREEMATERIAL_LGHTS1_MIST0_AMB2_SP1_TRUNK1");
        AssertThat(Ps(2, 3, false, true, false)).IsEqual("PSHADER_TREEMATERIAL_LGHTS_ANY_MIST_ANY_AMB2_SP_ANY_TRUNK1");
        AssertThat(Ps(0, 1, true, true, true)).IsEqual("PSHADER_TREEMATERIAL_LGHTS_ANY_MIST1_AMB0_SP0_TRUNK0");
        AssertThat(Ps(3, 1, false, true, false)).IsEqual(string.Empty);
        AssertThat(Vs(1, 1, false, true)).IsEqual("VSHADER_TREEMATERIAL_LGHTS1_AMB1_SP0_TRUNK0");
        AssertThat(Vs(2, 2, false, true)).IsEqual("VSHADER_TREEMATERIAL_LGHTS_ANY_AMB2_SP_ANY_TRUNK0");
        AssertThat(Vs(0, 4, true, false)).IsEqual("VSHADER_TREEMATERIAL_LGHTS_ANY_AMB0_SP1_TRUNK1");
    }

    [TestCase]
    public void RepeatedSelection()
    {
        AssertThat(Engine_ClassicTokensFable3.Entry(PS, Engine_ClassicTokensFable3.Repeated,
                Engine_ClassicTokensFable3.RepeatedPixelTokens(1, true, true, true)))
            .IsEqual("PSHADER_REPEATEDMATERIAL_LGHTS1_MIST1_GRASS1_NRM1");
        AssertThat(Engine_ClassicTokensFable3.Entry(PS, Engine_ClassicTokensFable3.Repeated,
                Engine_ClassicTokensFable3.RepeatedPixelTokens(0, false, false, true)))
            .IsEqual("PSHADER_REPEATEDMATERIAL_LGHTS_ANY_MIST0_GRASS0_NRM1");
        AssertThat(Engine_ClassicTokensFable3.Entry(VS, Engine_ClassicTokensFable3.Repeated,
                Engine_ClassicTokensFable3.RepeatedVertexTokens(true, false, true, true)))
            .IsEqual("VSHADER_REPEATEDMATERIAL_COLL1_GRASS0_NRM1_WIND1");
    }

    [TestCase]
    public void LightGroupsAndAmbientRekeyTheTokens()
    {
        List<string> base1 = Engine_ClassicTokensFable3.StandardPixelTokens(2, 1, false, false, true, false, false, false, false, false);
        List<string> group = Engine_ClassicTokensFable3.WithLights(base1, 4, false);
        AssertThat(Engine_ClassicTokensFable3.Entry(PS, Engine_ClassicTokensFable3.Standard, group))
            .IsEqual("PSHADER_STANDARDMATERIAL_LGHTS4_MIST0_AMB2_CMRPH0_SP1_STSP0_SH0_FR0_SELF0_ENV0");
        // An absent AMB token is AMB0, as Engine_ClassicMaterial's ambient retoken leaves it.
        List<string> noAmbient = base1.FindAll(t => !t.StartsWith("AMB", System.StringComparison.Ordinal));
        AssertThat(Engine_ClassicTokensFable3.Entry(PS, Engine_ClassicTokensFable3.Standard, noAmbient))
            .IsEqual("PSHADER_STANDARDMATERIAL_LGHTS_ANY_MIST_ANY_AMB0_CMRPH_ANY_SP_ANY_STSP_ANY_SH_ANY_FR_ANY_SELF_ANY_ENV_ANY");

        AssertThat(Engine_ClassicTokensFable3.AmbientFor(0, false)).IsEqual(2);
        AssertThat(Engine_ClassicTokensFable3.AmbientFor(1, true)).IsEqual(2);
        AssertThat(Engine_ClassicTokensFable3.AmbientFor(2, true)).IsEqual(3);
        AssertThat(Engine_ClassicTokensFable3.AmbientFor(2, false)).IsEqual(0);
    }

    [TestCase]
    public void OneLocalLightCountsTwo()
    {
        AssertThat(Engine_ClassicTokensFable3.GroupCount(1, false)).IsEqual(1);
        AssertThat(Engine_ClassicTokensFable3.GroupCount(1, true)).IsEqual(2);
        AssertThat(Engine_ClassicTokensFable3.GroupCount(3, false)).IsEqual(3);
        AssertThat(Engine_ClassicTokensFable3.GroupCount(9, false)).IsEqual(8);
        // One local light draws on LGHTS2; a lone sun on LGHTS1.
        List<string> local = Engine_ClassicTokensFable3.WithLights(
            Engine_ClassicTokensFable3.StandardPixelTokens(2, 1, false, false, false, false, false, false, false, false),
            Engine_ClassicTokensFable3.GroupCount(1, true), false);
        AssertThat(Engine_ClassicTokensFable3.Entry(PS, Engine_ClassicTokensFable3.Standard, local))
            .IsEqual("PSHADER_STANDARDMATERIAL_LGHTS2_MIST0_AMB2_CMRPH0_SP0_STSP0_SH0_FR0_SELF0_ENV0");
    }

    [TestCase]
    public void TheUberAxesArePublishedAsBooleansAndALoop()
    {
        List<string> tokens = Engine_ClassicTokensFable3.StandardPixelTokens(2, 6, true, false, true, true, true, true, false, false);
        uint expected = (1u << 16) | (1u << (16 + 3)) | (1u << (16 + 7)) | (1u << (16 + 9)) | (1u << (16 + 10)) |
                        (1u << (16 + 11));
        AssertThat(Engine_ClassicTokensFable3.Booleans(tokens)).IsEqual(expected);
        AssertThat(Engine_ClassicTokensFable3.Lights(tokens)).IsEqual(6);
        AssertThat(Engine_ClassicTokensFable3.LightBlocks(0)).IsEqual(0);
        AssertThat(Engine_ClassicTokensFable3.LightBlocks(1)).IsEqual(1);
        AssertThat(Engine_ClassicTokensFable3.LightBlocks(4)).IsEqual(1);
        AssertThat(Engine_ClassicTokensFable3.LightBlocks(5)).IsEqual(2);
        AssertThat(Engine_ClassicTokensFable3.LightBlocks(8)).IsEqual(2);
    }

    [TestCase]
    public void FamilyFollowsThePartsThatNameTheMaterial()
    {
        var info = new MdlInfo();
        var standard = new MdlMesh();
        standard.Materials.Add(new MdlMaterial());
        var tree = new MdlMesh();
        tree.Materials.Add(new MdlMaterial());
        var scatter = new MdlMesh();
        scatter.Materials.Add(new MdlMaterial());
        info.Meshes.Add(standard);
        info.Meshes.Add(tree);
        info.Meshes.Add(scatter);
        info.MeshBuffers.Add(new MdlMeshBuffer { MeshIndex = 0, IsEngineRigid = true });
        info.MeshBuffers.Add(new MdlMeshBuffer { MeshIndex = 1, IsEngineTree = true });
        info.MeshBuffers.Add(new MdlMeshBuffer { MeshIndex = 2, IsEngineRigid = true, IsEngineInstanced = true });

        AssertThat(Engine_ClassicTokensFable3.FamilyFor(standard.Materials[0], info)).IsEqual(Engine_ClassicTokensFable3.Standard);
        AssertThat(Engine_ClassicTokensFable3.FamilyFor(tree.Materials[0], info)).IsEqual(Engine_ClassicTokensFable3.Tree);
        AssertThat(Engine_ClassicTokensFable3.FamilyFor(scatter.Materials[0], info)).IsEqual(Engine_ClassicTokensFable3.Repeated);
    }

    [TestCase]
    public void SpecularNeedsAStatedColour()
    {
        AssertThat(Engine_ClassicTokensFable3.SpecularStated(new MdlMaterial())).IsFalse();
        var dim = new MdlMaterial
        {
            StatedProperties = new Dictionary<string, float[]> { ["SpecularColour"] = new[] { 0.004f, 0.004f, 0.004f, 1f } },
        };
        AssertThat(Engine_ClassicTokensFable3.SpecularStated(dim)).IsFalse();
        var lit = new MdlMaterial
        {
            StatedProperties = new Dictionary<string, float[]> { ["SpecularColour"] = new[] { 0.2f, 0.2f, 0.2f, 1f } },
        };
        AssertThat(Engine_ClassicTokensFable3.SpecularStated(lit)).IsTrue();
    }
}
