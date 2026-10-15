# 0.66 : fabrique le modele "solide" du cube de detritus de la Vagabonde (voir skse/src/Solids.cpp).
#
# C'est un modele Skyrim Special Edition SANS rien a dessiner : seulement une boite de collision Havok
# (bhkCollisionObject -> bhkRigidBody -> bhkBoxShape), fixe, de la couche "statique" de Skyrim. Elle arrete le
# joueur, les ennemis, les fleches et les sorts, et on peut monter dessus.
# Format verifie champ par champ dans nif.xml de niftools (version 20.2.0.7, BSVER 100).
#
# Le script ecrit skse/src/SolidNif.h : les octets du modele (boite de 1 m), et la place des 3 demi-cotes de la
# boite. Le plugin SKSE ecrit au lancement une copie par taille (Solide_040.nif ... Solide_200.nif).
#
# Utilisation (depuis le dossier du projet) :  python tools/make_solid_nif.py

import os
import struct

STONE = 3741512247   # SKY_HAV_MAT_STONE
RADIUS = 0.05        # coque autour de la boite (unites Havok, environ des metres)
LAYER_STATIC = 1     # SKYL_STATIC


def sized(text):
    data = text.encode('ascii')
    return struct.pack('<I', len(data)) + data


def export_string(text):
    data = text.encode('ascii')
    return struct.pack('<B', len(data) + 1) + data + b'\x00'


def filter_info():
    return struct.pack('<BBH', LAYER_STATIC, 0, 0)  # couche, drapeaux, groupe


def build(half):
    strings = ['SolideRoR2', 'BSX']
    types = ['NiNode', 'BSXFlags', 'bhkCollisionObject', 'bhkRigidBody', 'bhkBoxShape']
    blocks = []

    # 0 : NiNode (racine)
    node = struct.pack('<i', 0)                      # nom
    node += struct.pack('<Ii', 1, 1)                 # 1 donnee en plus : le bloc 1 (BSX)
    node += struct.pack('<i', -1)                    # controleur
    node += struct.pack('<I', 14)                    # drapeaux
    node += struct.pack('<3f', 0, 0, 0)              # position
    node += struct.pack('<9f', 1, 0, 0, 0, 1, 0, 0, 0, 1)
    node += struct.pack('<f', 1.0)                   # echelle
    node += struct.pack('<i', 2)                     # collision : le bloc 2
    node += struct.pack('<I', 0)                     # enfants
    node += struct.pack('<I', 0)                     # effets
    blocks.append((0, node))

    # 1 : BSXFlags = 2 (Havok)
    blocks.append((1, struct.pack('<iI', 1, 2)))

    # 2 : bhkCollisionObject : cible = racine, drapeaux 0x81 (actif, synchro), corps = bloc 3
    blocks.append((2, struct.pack('<iHi', 0, 0x81, 3)))

    # 3 : bhkRigidBody fixe
    body = struct.pack('<i', 4)                      # forme : bloc 4
    body += filter_info()
    body += b'\x00' * 4 + struct.pack('<B', 1) + b'\x00' * 3   # bhkWorldObjectCInfo (phase large : entite)
    body += struct.pack('<III', 0, 0, 0x80000000)    # propriete
    body += struct.pack('<BBH', 1, 0, 0xFFFF)        # bhkEntityCInfo
    # bhkRigidBodyCInfo2010
    body += b'\x00' * 4
    body += filter_info()
    body += b'\x00' * 4
    body += struct.pack('<I', 0)                     # Unknown Int 1
    body += struct.pack('<BBH', 1, 0, 0xFFFF)        # reponse, inutilise, delai
    body += struct.pack('<4f', 0, 0, 0, 0)           # translation
    body += struct.pack('<4f', 0, 0, 0, 1)           # rotation
    body += struct.pack('<4f', 0, 0, 0, 0)           # vitesse lineaire
    body += struct.pack('<4f', 0, 0, 0, 0)           # vitesse angulaire
    body += struct.pack('<12f', *([0.0] * 12))       # inertie
    body += struct.pack('<4f', 0, 0, 0, 0)           # centre
    body += struct.pack('<f', 0.0)                   # masse (0 : immobile)
    body += struct.pack('<ff', 0.1, 0.05)            # amortissements
    body += struct.pack('<ff', 1.0, 1.0)             # temps, gravite
    body += struct.pack('<ff', 0.5, 0.0)             # frottement, roulement
    body += struct.pack('<f', 0.4)                   # rebond
    body += struct.pack('<ff', 104.4, 31.57)         # vitesses max
    body += struct.pack('<f', 0.15)                  # penetration
    body += struct.pack('<BBBB', 7, 1, 1, 1)         # mouvement FIXE, jamais desactive, solveur off, qualite FIXE
    body += struct.pack('<BBBB', 0, 0, 3, 0)
    body += b'\x00' * 12
    body += struct.pack('<I', 0)                     # contraintes
    body += struct.pack('<H', 0)                     # drapeaux du corps
    blocks.append((3, body))

    # 4 : bhkBoxShape
    box = struct.pack('<If', STONE, RADIUS) + b'\x00' * 8
    dims_offset_in_block = len(box)
    box += struct.pack('<3f', half, half, half) + struct.pack('<f', 0.0)
    blocks.append((4, box))

    out = b'Gamebryo File Format, Version 20.2.0.7\n'
    out += struct.pack('<I', 0x14020007)
    out += struct.pack('<B', 1)
    out += struct.pack('<I', 12)
    out += struct.pack('<I', len(blocks))
    out += struct.pack('<I', 100)
    out += export_string('SkyrimRoR2') + export_string('') + export_string('')
    out += struct.pack('<H', len(types))
    for t in types:
        out += sized(t)
    for t, _ in blocks:
        out += struct.pack('<H', t)
    for _, b in blocks:
        out += struct.pack('<I', len(b))
    out += struct.pack('<I', len(strings))
    out += struct.pack('<I', max(len(s) for s in strings))
    for s in strings:
        out += sized(s)
    out += struct.pack('<I', 0)                      # groupes
    starts = []
    for _, b in blocks:
        starts.append(len(out))
        out += b
    out += struct.pack('<I', 1) + struct.pack('<i', 0)  # une racine : le bloc 0
    return out, starts[4] + dims_offset_in_block


def main():
    data, dims = build(0.5 - RADIUS)
    assert struct.unpack_from('<3f', data, dims) == struct.unpack('<3f', struct.pack('<3f', *([0.5 - RADIUS] * 3)))
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, '..', 'skse', 'src', 'SolidNif.h')
    lines = ['#pragma once', '', '// Genere par tools/make_solid_nif.py : ne pas modifier a la main.',
             '// Modele Skyrim SE avec une seule boite de collision Havok (fixe, couche statique), de 1 m de cote.',
             '', '#include <array>', '#include <cstdint>', '', 'namespace skyrimror2::SolidNif', '{',
             '\tinline constexpr std::size_t kDimensionsOffset = %d;  // 3 floats : demi-cotes (unites Havok)' % dims,
             '\tinline constexpr float       kRadius = %.2ff;          // coque Havok autour de la boite' % RADIUS,
             '\tinline constexpr std::array<std::uint8_t, %d> kBytes{' % len(data)]
    for i in range(0, len(data), 16):
        lines.append('\t\t' + ', '.join('0x%02X' % b for b in data[i:i + 16]) + ',')
    lines += ['\t};', '}', '']
    with open(path, 'w', encoding='ascii', newline='\n') as f:
        f.write('\n'.join(lines))
    print('Ecrit : %s (%d octets, demi-cotes a %d)' % (os.path.normpath(path), len(data), dims))


if __name__ == '__main__':
    main()
