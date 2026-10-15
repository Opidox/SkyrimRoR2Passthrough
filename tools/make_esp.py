# Fabrique SkyrimRoR2.esp : un petit fichier de donnees Skyrim (marque ESL, il ne prend pas de place
# dans l'ordre de chargement) qui contient des POUVOIRS MINEURS generiques et des OBJETS DIVERS generiques.
#
# Chaque pouvoir correspond a une case "competence X, variante Y" (la 5e "competence" : le skin). Le plugin SKSE les renomme selon
# le survivant en cours ("RoR2 2 Secondaire - Fleche laser") et ne donne au joueur que ceux qui existent.
# Choisir un de ces pouvoirs dans le menu Magie > Pouvoirs change la variante dans RoR2.
#
# Utilisation (depuis le dossier du projet) :  python tools/make_esp.py
# Le fichier est ecrit dans skse/SkyrimRoR2.esp

import os
import struct

SLOTS = 9                 # principale, secondaire, utilitaire, speciale, le skin, puis 4 cases en plus
VARIANTS = 5              # variantes maximum par competence
FIRST_ID = 0x800          # premier numero d'objet (plage des fichiers ESL : 0x800 a 0xFFF)
MASTER_INDEX = 0x01       # nos objets : apres l'unique fichier maitre (Skyrim.esm)

VOICE_EQUIP = 0x00025BEE      # emplacement "voix" (cris et pouvoirs), dans Skyrim.esm
RESTORE_HEALTH = 0x0003EB15   # effet "restaurer la sante" des potions, dans Skyrim.esm (ici a 0 : sans effet)


def sub(tag, data):
    return tag.encode('ascii') + struct.pack('<H', len(data)) + data


def zstring(text):
    return text.encode('cp1252') + b'\x00'


def record(tag, form_id, payload, flags=0):
    # en-tete de 24 octets : type, taille, drapeaux, numero, controle de version, version du format (44)
    return tag.encode('ascii') + struct.pack('<IIIIHH', len(payload), flags, form_id, 0, 44, 0) + payload


def spell(slot, variant):
    local = FIRST_ID + slot * VARIANTS + variant
    form_id = (MASTER_INDEX << 24) | local
    if slot < 4:
        editor_id = 'SkyrimRoR2_C%d_V%d' % (slot + 1, variant + 1)
        name = 'RoR2 skill %d variant %d' % (slot + 1, variant + 1)
    elif slot == 4:
        editor_id = 'SkyrimRoR2_Skin%d' % (variant + 1)
        name = 'RoR2 skin %d' % (variant + 1)
    else:
        editor_id = 'SkyrimRoR2_X%d_V%d' % (slot + 1, variant + 1)
        name = 'RoR2 slot %d variant %d' % (slot + 1, variant + 1)
    spit = struct.pack(
        '<IIIfIIffI',
        0,                       # cout
        0x00000001 | 0x00800000, # cout manuel, pas d'absorption/renvoi
        3,                       # type : pouvoir mineur
        0.0,                     # temps de charge
        1,                       # lancement : tir unique
        0,                       # cible : soi-meme
        0.0,                     # duree de lancement
        0.0,                     # portee
        0)                       # atout "demi-cout" : aucun
    payload = (
        sub('EDID', zstring(editor_id)) +
        sub('OBND', b'\x00' * 12) +
        sub('FULL', zstring(name)) +
        sub('ETYP', struct.pack('<I', VOICE_EQUIP)) +
        sub('DESC', zstring('Switches the variant of this RoR2 survivor skill.')) +
        sub('SPIT', spit) +
        sub('EFID', struct.pack('<I', RESTORE_HEALTH)) +
        sub('EFIT', struct.pack('<fII', 0.0, 0, 0))
    )
    return record('SPEL', form_id, payload)


# 0.61 : les objets RoR2 dans l'inventaire de Skyrim. Des OBJETS DIVERS generiques (poids 0, valeur 0) ; le plugin
# SKSE leur donne le nom et l'apparence de l'objet RoR2 qu'ils representent (voir Items.cpp, objets_skyrim.txt).
ITEM_SLOTS = 600
FIRST_ITEM_ID = 0x900     # 0x900 a 0xB57


def misc(index):
    local = FIRST_ITEM_ID + index
    form_id = (MASTER_INDEX << 24) | local
    payload = (
        sub('EDID', zstring('SkyrimRoR2_Objet%03d' % index)) +
        sub('OBND', struct.pack('<hhhhhh', -4, -4, -2, 4, 4, 2)) +
        sub('FULL', zstring('RoR2 item %d' % index)) +
        sub('DATA', struct.pack('<If', 0, 0.0))  # valeur 0, poids 0
    )
    return record('MISC', form_id, payload)


# 0.63 : les effets des atouts de Destruction sur les competences RoR2 (voir skse/src/Experience.cpp).
# 4 effets magiques et 4 sorts, lances par le plugin SKSE sur l'ennemi touche (au nom du joueur) :
#   brulure (feu), ralentissement (gel), fuite (Flammes intenses), paralysie (Gel profond).
EFFECT_FIRST_ID = 0xC00   # effets magiques 0xC00 a 0xC03
EFFECT_SPELL_ID = 0xC10   # sorts 0xC10 a 0xC13
DESTRUCTION_AV = 20
# (nom, archetype, valeur touchee, drapeaux, magnitude, duree)
#   archetypes : 0 modifier une valeur, 7 demoraliser (fuite), 21 paralysie
#   valeurs : 1 confiance, 24 sante, 30 vitesse, 53 paralysie
#   drapeaux : 1 hostile, 2 se retablit a la fin, 4 nuisible
EFFECTS = [
    ('Brulure', 0, 24, 0x1 | 0x4, 1.0, 3),
    ('Ralenti', 0, 30, 0x1 | 0x2 | 0x4, 50.0, 3),
    ('Fuite', 7, 1, 0x1 | 0x4, 200.0, 8),
    ('Paralysie', 21, 53, 0x1 | 0x4, 0.0, 4),
]


def magic_effect(index):
    name, archetype, actor_value, flags, _, _ = EFFECTS[index]
    form_id = (MASTER_INDEX << 24) | (EFFECT_FIRST_ID + index)
    data = struct.pack(
        '<IfIiiHHIfIIiiffffIiIIIIiIIIfIfIIIIIIIff',
        flags,            # drapeaux
        0.0,              # cout de base
        0,                # objet associe
        DESTRUCTION_AV,   # ecole de magie
        -1,               # resistance : aucune (les degats viennent deja de RoR2)
        0, 0,             # contre-effets, bourrage
        0,                # lumiere
        0.0,              # poids de la diminution
        0, 0,             # shaders d'effet et d'enchantement
        0, 0,             # niveau minimum, zone
        0.0, 0.0, 0.0, 0.0,  # temps de charge, courbe, duree de la diminution, poids 2e valeur
        archetype,
        actor_value,
        0, 0,             # projectile, explosion
        1,                # lancement : tir unique
        3,                # cible : acteur vise
        -1,               # 2e valeur : aucune
        0, 0, 0,          # art de lancement, art d'impact, impacts
        0.0,              # experience de competence : aucune (le plugin la donne deja)
        0, 1.0,           # double lancement
        0, 0, 0,          # art d'enchantement, visuels
        0, 0, 0,          # capacite, modificateur d'image, atout
        0,                # niveau sonore
        0.0, 0.0)         # IA
    assert len(data) == 152, len(data)
    payload = (
        sub('EDID', zstring('SkyrimRoR2_Effet' + name)) +
        sub('FULL', zstring('RoR2 ' + name)) +
        sub('DATA', data) +
        sub('DNAM', zstring(''))
    )
    return record('MGEF', form_id, payload)


def effect_spell(index):
    name, _, _, _, magnitude, duration = EFFECTS[index]
    form_id = (MASTER_INDEX << 24) | (EFFECT_SPELL_ID + index)
    spit = struct.pack(
        '<IIIfIIffI',
        0,                       # cout
        0x00000001 | 0x00800000, # cout manuel
        0,                       # type : sort
        0.0,
        1,                       # tir unique
        3,                       # acteur vise
        0.0, 0.0, 0)
    payload = (
        sub('EDID', zstring('SkyrimRoR2_Sort' + name)) +
        sub('OBND', b'\x00' * 12) +
        sub('FULL', zstring('RoR2 ' + name)) +
        sub('SPIT', spit) +
        sub('EFID', struct.pack('<I', (MASTER_INDEX << 24) | (EFFECT_FIRST_ID + index))) +
        sub('EFIT', struct.pack('<fII', magnitude, 0, duration))
    )
    return record('SPEL', form_id, payload)


# 0.66 : des objets fixes INVISIBLES, faits seulement d'une boite de collision : le cube de detritus de la
# Vagabonde devient solide dans Skyrim (voir skse/src/Solids.cpp et tools/make_solid_nif.py). Une taille par objet
# (en cm) ; le plugin pose celui dont la taille est la plus proche du cube.
SOLID_FIRST_ID = 0xC20    # 0xC20 a 0xC26
SOLID_SIZES = [40, 60, 80, 100, 125, 150, 200]


def solid(index):
    size = SOLID_SIZES[index]
    form_id = (MASTER_INDEX << 24) | (SOLID_FIRST_ID + index)
    half = int(round(size / 100.0 * 70.0 / 2.0))
    payload = (
        sub('EDID', zstring('SkyrimRoR2_Solide%03d' % size)) +
        sub('OBND', struct.pack('<hhhhhh', -half, -half, -half, half, half, half)) +
        sub('MODL', zstring('SkyrimRoR2\\Solide_%03d.nif' % size)) +
        sub('DNAM', struct.pack('<fI', 30.0, 0))
    )
    return record('STAT', form_id, payload)


# 0.71 : le livre "Journal RoR2". Son texte est remplace par le plugin SKSE a chaque lecture (objets trouves).
JOURNAL_ID = 0xC30


def journal():
    form_id = (MASTER_INDEX << 24) | JOURNAL_ID
    payload = (
        sub('EDID', zstring('SkyrimRoR2_Journal')) +
        sub('OBND', struct.pack('<hhhhhh', -8, -11, -2, 8, 11, 2)) +
        sub('FULL', zstring('RoR2 Logbook')) +
        sub('DESC', zstring('<p align="center">RoR2 Logbook</p><p>Close this book: the list of the RoR2 items you found opens.</p>')) +
        sub('DATA', struct.pack('<BBHiIf', 0, 0, 0, -1, 0, 0.0))  # livre, n'apprend rien, valeur 0, poids 0
    )
    return record('BOOK', form_id, payload)


# 0.72 : les EQUIPEMENTS RoR2 dans l'inventaire de Skyrim : des "amulettes" generiques (vetement, sans modele porte),
# sur l'emplacement 55 (bijou du visage, libre en jeu normal) : on en garde autant qu'on veut, celle qu'on porte est
# l'equipement actif dans RoR2. Le plugin SKSE leur donne le nom de l'equipement (voir Items.cpp).
EQUIP_SLOTS = 160
FIRST_EQUIP_ID = 0xD00    # 0xD00 a 0xD9F
SLOT_55 = 1 << 25


def amulet(index):
    form_id = (MASTER_INDEX << 24) | (FIRST_EQUIP_ID + index)
    payload = (
        sub('EDID', zstring('SkyrimRoR2_Equipement%03d' % index)) +
        sub('OBND', struct.pack('<hhhhhh', -4, -4, -1, 4, 4, 1)) +
        sub('FULL', zstring('RoR2 equipment %d' % index)) +
        sub('BOD2', struct.pack('<II', SLOT_55, 2)) +         # emplacement 55, vetement
        sub('RNAM', struct.pack('<I', 0x00000019)) +          # race par defaut
        sub('DATA', struct.pack('<if', 0, 0.0)) +             # valeur 0, poids 0
        sub('DNAM', struct.pack('<i', 0))                     # armure 0
    )
    return record('ARMO', form_id, payload)


def group(tag, records):
    body = b''.join(records)
    return b'GRUP' + struct.pack('<I', 24 + len(body)) + tag.encode('ascii') + struct.pack('<iHHHH', 0, 0, 0, 0, 0) + body


def main():
    spells = [spell(s, v) for s in range(SLOTS) for v in range(VARIANTS)]
    items = [misc(i) for i in range(ITEM_SLOTS)]
    effects = [magic_effect(i) for i in range(len(EFFECTS))]
    spells += [effect_spell(i) for i in range(len(EFFECTS))]
    solids = [solid(i) for i in range(len(SOLID_SIZES))]
    books = []  # 0.72 : le livre du journal faisait planter Skyrim a l'ouverture : retire (le journal s'ouvre avec une touche)
    amulets = [amulet(i) for i in range(EQUIP_SLOTS)]
    groups = (group('MGEF', effects) + group('SPEL', spells) + group('ARMO', amulets) + group('MISC', items) +
              group('STAT', solids))

    header = (
        sub('HEDR', struct.pack('<fII', 1.71, len(effects) + len(spells) + len(items) + len(solids) + len(books) + len(amulets) + 1, FIRST_EQUIP_ID + EQUIP_SLOTS)) +
        sub('CNAM', zstring('Opidox & Claude')) +
        sub('SNAM', zstring('Skill variant powers and RoR2 items for SkyrimRoR2')) +
        sub('MAST', zstring('Skyrim.esm')) +
        sub('DATA', struct.pack('<Q', 0))
    )
    tes4 = record('TES4', 0, header, flags=0x200)  # 0x200 : fichier "leger" (ESL)

    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, '..', 'skse', 'SkyrimRoR2.esp')
    with open(out, 'wb') as f:
        f.write(tes4 + groups)
    print('Ecrit : %s (%d sorts et pouvoirs, %d effets, %d objets, %d solides, %d livre, %d equipements)' % (os.path.normpath(out), len(spells), len(effects), len(items), len(solids), len(books), len(amulets)))


if __name__ == '__main__':
    main()
