"""Check the terrain's complete shader source rather than its top-level text alone."""
from pathlib import Path
import re
import unittest

GODOT = Path(__file__).resolve().parents[3] / 'Godot'


def expand(path, stack=(), guards=None):
    """Expand production resource includes, honouring include guards, and reject recursion.

    A file that opens with `#ifndef NAME` / `#define NAME` compiles once however many
    includes reach it, as in Godot, so its second expansion is empty.
    """
    if path in stack:
        raise ValueError('Recursive shader include: ' + str(path))
    guards = set() if guards is None else guards
    text = path.read_text(encoding='utf-8')
    guard = re.match(r'\s*#ifndef\s+(\w+)\s*\n\s*#define\s+\1\b', text)
    if guard:
        if guard[1] in guards:
            return ''
        guards.add(guard[1])
    return re.sub(r'^\s*#include\s+"res://([^\"]+)"\s*$',
                  lambda match: expand(GODOT / match[1], (*stack, path), guards), text, flags=re.M)


class TerrainShaderIncludeTests(unittest.TestCase):
    """The full lighting/atmosphere/terrain lifecycle must have one diagnostic declaration."""

    def test_shared_globals_are_declared_once_after_include_expansion(self):
        """Catch the real shader rejection caused by duplicate globals hidden behind an include."""
        source = expand(GODOT / 'src/Engine/level/terrain/terrain_splat.gdshader')
        declarations = re.findall(r'\bglobal\s+uniform\s+\w+\s+(\w+)\s*[;=]', source)
        self.assertEqual(len(declarations), len(set(declarations)), 'Duplicate shader global declarations')
        self.assertEqual(declarations.count('fable_shader_cost_diagnostic'), 1)
        self.assertIn('fable_shader_cost_diagnostic & 1', source)
        self.assertIn('fable_shader_cost_diagnostic & 8', source)


if __name__ == '__main__':
    unittest.main()
