using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Audio;
using Maple.Classic.Repacker.Engine.Canvas;

namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// Builds realistic MapleStory Classic World (<c>v779</c>) sample WZ archives in memory with
/// multi-frame pixel-art sprites, vectors, UOLs, <c>_outlink</c> references, string tables, and playable <c>Sound_DX8</c> effects.
/// </summary>
public static class SampleWorkspaceGenerator {
    public static List<(string Name, byte[] ArchiveBytes)> BuildSampleArchives() {
        return [
            ("Character_000.wz", BuildCharacterArchive()),
            ("Character_Canvas_000.wz", BuildCharacterCanvasArchive()),
            ("Mob_000.wz", BuildMobArchive()),
            ("String_000.wz", BuildStringArchive()),
            ("Sound_000.wz", BuildSoundArchive()),
        ];
    }

    private static byte[] BuildCharacterArchive() {
        using var ar = new WzArchive();

        // 1. Coat/01040000.img (White Undershirt)
        var coat = new WzValue.Object {
            ["info/islot"] = "Ma",
            ["info/vslot"] = "Ma",
            ["info/reqJob"] = 0,
            ["info/reqLevel"] = 10,
            ["info/reqSTR"] = 0,
            ["info/reqDEX"] = 0,
            ["info/reqINT"] = 0,
            ["info/reqLUK"] = 0,
            ["info/incPDD"] = 12,
            ["info/tuc"] = 7,
            ["info/price"] = 300,
            ["info/cash"] = 0,
            ["info/icon"] = CreateCoatCanvas(0, 32, 32),
            ["info/iconRaw"] = CreateCoatCanvas(0, 30, 30),
            ["stand1/0"] = CreateCoatCanvas(0, 32, 32, delay: 450, originX: 16, originY: 28),
            ["stand1/1"] = CreateCoatCanvas(1, 32, 32, delay: 450, originX: 16, originY: 27),
            ["stand1/2"] = CreateCoatCanvas(2, 32, 32, delay: 450, originX: 16, originY: 28),
            ["stand2"] = new WzValue.Uol("stand1"),
        };
        ar.SetImage("Coat/01040000.img", coat);

        // 2. Weapon/01302000.img (Classic Sword)
        var sword = new WzValue.Object {
            ["info/islot"] = "Wp",
            ["info/vslot"] = "Wp",
            ["info/walk"] = 1,
            ["info/stand"] = 1,
            ["info/attack"] = new WzValue.Short(1),
            ["info/reqJob"] = 1,
            ["info/reqLevel"] = 15,
            ["info/reqSTR"] = 45,
            ["info/incPAD"] = 32,
            ["info/attackSpeed"] = 5,
            ["info/tuc"] = 7,
            ["info/price"] = 1500,
            ["info/cash"] = 0,
            ["info/icon"] = CreateSwordCanvas(0, 36, 36, delay: 180),
            ["swingO1/0"] = CreateSwordCanvas(0, 36, 36, delay: 180),
            ["swingO1/1"] = CreateSwordCanvas(1, 36, 36, delay: 160),
            ["swingO1/2"] = CreateSwordCanvas(2, 36, 36, delay: 240),
        };
        ar.SetImage("Weapon/01302000.img", sword);

        // 3. PetEquip/01802653.img (Outlinked Staff)
        var stubPayload = WzCanvasCodec.EncodeFromRgba(1, 1, 2, [0, 0, 0, 0]);
        var petEquip = new WzValue.Object {
            ["info/islot"] = "Pe",
            ["info/vslot"] = "Pe",
            ["info/tuc"] = 8,
            ["info/cash"] = 1,
            ["5002828/stand0/0"] = new WzValue.Canvas(
                1,
                1,
                2,
                stubPayload,
                [
                    new WzProperty("origin", (18, 30)),
                    new WzProperty("z", "petEquip"),
                    new WzProperty("delay", 220),
                    new WzProperty("_outlink", "Character/PetEquip/_Canvas/01802653.img/5002828/stand0/0"),
                ]),
        };
        ar.SetImage("PetEquip/01802653.img", petEquip);

        return ar.ToBytes();
    }

    private static byte[] BuildCharacterCanvasArchive() {
        using var ar = new WzArchive();
        var canvasImg = new WzValue.Object {
            ["5002828/stand0/0"] = CreateStaffCanvas(36, 36),
        };
        ar.SetImage("01802653.img", canvasImg);
        return ar.ToBytes();
    }

    private static byte[] BuildMobArchive() {
        using var ar = new WzArchive();
        var mushroom = new WzValue.Object {
            ["info/level"] = 8,
            ["info/bodyAttack"] = 1,
            ["info/maxHP"] = 80,
            ["info/maxMP"] = 10,
            ["info/speed"] = -10,
            ["info/PADamage"] = 32,
            ["info/PDDamage"] = 10,
            ["info/MADamage"] = 0,
            ["info/MDDamage"] = 10,
            ["info/acc"] = 40,
            ["info/eva"] = 3,
            ["info/exp"] = 15,
            ["info/undead"] = 0,
            ["info/pushed"] = 10,
            ["stand/0"] = CreateMushroomCanvas(0, 48, 48, delay: 220, bounceY: 0),
            ["stand/1"] = CreateMushroomCanvas(1, 48, 48, delay: 220, bounceY: 2),
            ["move/0"] = CreateMushroomCanvas(0, 48, 48, delay: 140, bounceY: 0),
            ["move/1"] = CreateMushroomCanvas(1, 48, 48, delay: 120, bounceY: -6),
            ["move/2"] = CreateMushroomCanvas(2, 48, 48, delay: 140, bounceY: -10),
            ["move/3"] = CreateMushroomCanvas(3, 48, 48, delay: 140, bounceY: -3),
            ["hit1/0"] = CreateMushroomCanvas(2, 48, 48, delay: 400, bounceY: -4),
            ["die1/0"] = new WzValue.Uol("../hit1/0"),
        };
        ar.SetImage("01210102.img", mushroom);
        return ar.ToBytes();
    }

    private static byte[] BuildStringArchive() {
        using var ar = new WzArchive();
        var eqp = new WzValue.Object {
            ["Eqp/Coat/1040000/name"] = "White Undershirt",
            ["Eqp/Coat/1040000/desc"] = "A crisp cotton undershirt worn by beginners on Maple Island.",
            ["Eqp/Longcoat/1054555/name"] = "Frieren's Clothes",
            ["Eqp/Longcoat/1054555/desc"] = "Traveler attire embued with ancient elven warding magic.",
            ["Eqp/Weapon/1302000/name"] = "Classic Broadsword",
            ["Eqp/Weapon/1302000/desc"] = "A balanced one-handed sword forged in Perion.",
            ["Eqp/PetEquip/1802653/name"] = "Mage's Crimson Staff",
            ["Eqp/PetEquip/1802653/desc"] = "Focuses mana into concentrated bursts.",
        };
        ar.SetImage("Eqp.img", eqp);

        var mob = new WzValue.Object {
            ["100100/name"] = "Snail",
            ["1210102/name"] = "Orange Mushroom",
            ["8800000/name"] = "Zakum",
        };
        ar.SetImage("Mob.img", mob);

        var map = new WzValue.Object {
            ["victoria/100000000/streetName"] = "Victoria Road",
            ["victoria/100000000/mapName"] = "Henesys",
            ["victoria/101000000/streetName"] = "Victoria Road",
            ["victoria/101000000/mapName"] = "Ellinia",
            ["victoria/102000000/streetName"] = "Victoria Road",
            ["victoria/102000000/mapName"] = "Perion",
            ["victoria/103000000/streetName"] = "Victoria Road",
            ["victoria/103000000/mapName"] = "Kerning City",
        };
        ar.SetImage("Map.img", map);

        return ar.ToBytes();
    }

    private static byte[] BuildSoundArchive() {
        using var ar = new WzArchive();
        var bgm = new WzValue.Object {
            ["FloralLife"] = WzSoundCodec.CreateSynthesizedChimeSound(523, 659, 480),
            ["GoPicnic"] = WzSoundCodec.CreateSynthesizedChimeSound(587, 784, 480),
            ["AboveTheTreetops"] = WzSoundCodec.CreateSynthesizedChimeSound(440, 659, 520),
        };
        ar.SetImage("Bgm00.img", bgm);

        var mobSfx = new WzValue.Object {
            ["01210102/Jump"] = WzSoundCodec.CreateSynthesizedChimeSound(392, 587, 240),
            ["01210102/Damage"] = WzSoundCodec.CreateSynthesizedChimeSound(330, 262, 220),
            ["01210102/Die"] = WzSoundCodec.CreateSynthesizedChimeSound(494, 247, 380),
        };
        ar.SetImage("Mob.img", mobSfx);
        return ar.ToBytes();
    }

    private static WzValue.Canvas CreateCoatCanvas(int frame, int w, int h, int delay = 300, int originX = 16, int originY = 28) {
        byte[] rgba = new byte[w * h * 4];
        int yShift = frame == 1 ? 1 : 0;
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int sy = y - yShift;
                bool body = sy >= 7 && sy <= 26 && x >= 8 && x <= 23;
                bool sleeveL = sy >= 8 && sy <= 17 && x >= 4 && x <= 8;
                bool sleeveR = sy >= 8 && sy <= 17 && x >= 23 && x <= 27;
                bool collar = sy >= 6 && sy <= 9 && x >= 12 && x <= 19;
                if (!body && !sleeveL && !sleeveR) continue;
                if (collar && sy == 6) continue;

                bool border = sy == 7 || sy == 26 || x == 8 || x == 23 ||
                              (sleeveL && (x == 4 || sy == 8 || sy == 17)) ||
                              (sleeveR && (x == 27 || sy == 8 || sy == 17));
                bool trim = sy == 25 || (collar && sy == 9);
                int idx = (y * w + x) * 4;
                if (border) {
                    SetPixel(rgba, idx, 42, 54, 78, 255);
                } else if (trim) {
                    SetPixel(rgba, idx, 56, 152, 255, 255);
                } else {
                    byte shade = (byte) (235 - Math.Abs(x - 15) * 3 - (sy - 7) * 2);
                    SetPixel(rgba, idx, shade, (byte) (shade + 4), 250, 255);
                }
            }
        }

        byte[] payload = WzCanvasCodec.EncodeFromRgba(w, h, 2, rgba);
        return new WzValue.Canvas(
            w,
            h,
            2,
            payload,
            [
                new WzProperty("origin", (originX, originY)),
                new WzProperty("z", "mailChest"),
                new WzProperty("delay", delay),
            ]);
    }

    private static WzValue.Canvas CreateSwordCanvas(int frame, int w, int h, int delay) {
        byte[] rgba = new byte[w * h * 4];
        for (int i = 6; i < 29; i++) {
            int bx = frame switch {
                0 => i,
                1 => w - 1 - i,
                _ => i,
            };
            int by = frame switch {
                0 => h - 1 - i,
                1 => h - 1 - i,
                _ => i,
            };
            for (int dy = -2; dy <= 2; dy++) {
                for (int dx = -2; dx <= 2; dx++) {
                    if (Math.Abs(dx) + Math.Abs(dy) > 2) continue;
                    int px = bx + dx;
                    int py = by + dy;
                    if (px < 0 || px >= w || py < 0 || py >= h) continue;
                    int idx = (py * w + px) * 4;
                    bool isHilt = i < 12;
                    bool isGuard = i is >= 11 and <= 13;
                    if (isGuard) {
                        SetPixel(rgba, idx, 245, 190, 60, 255);
                    } else if (isHilt) {
                        SetPixel(rgba, idx, 140, 75, 45, 255);
                    } else if (Math.Abs(dx) + Math.Abs(dy) == 2) {
                        SetPixel(rgba, idx, 70, 95, 130, 255);
                    } else {
                        SetPixel(rgba, idx, 215, 235, 255, 255);
                    }
                }
            }
        }

        byte[] payload = WzCanvasCodec.EncodeFromRgba(w, h, 2, rgba);
        return new WzValue.Canvas(
            w,
            h,
            2,
            payload,
            [
                new WzProperty("origin", (18, 28)),
                new WzProperty("lt", (-18, -28)),
                new WzProperty("rb", (18, 8)),
                new WzProperty("z", "weapon"),
                new WzProperty("delay", delay),
            ]);
    }

    private static WzValue.Canvas CreateStaffCanvas(int w, int h) {
        byte[] rgba = new byte[w * h * 4];
        for (int y = 4; y < h - 4; y++) {
            for (int x = 16; x <= 19; x++) {
                int idx = (y * w + x) * 4;
                SetPixel(rgba, idx, 135, 85, 50, 255);
            }
        }
        // Crimson gem orb at top
        for (int y = 3; y <= 13; y++) {
            for (int x = 12; x <= 23; x++) {
                double dist = Math.Sqrt((x - 17.5) * (x - 17.5) + (y - 8.0) * (y - 8.0));
                if (dist <= 5.5) {
                    int idx = (y * w + x) * 4;
                    if (dist > 4.3) {
                        SetPixel(rgba, idx, 245, 200, 75, 255);
                    } else {
                        SetPixel(rgba, idx, 235, 55, 85, 255);
                    }
                }
            }
        }

        byte[] payload = WzCanvasCodec.EncodeFromRgba(w, h, 2, rgba);
        return new WzValue.Canvas(
            w,
            h,
            2,
            payload,
            [
                new WzProperty("origin", (18, 30)),
                new WzProperty("z", "petEquip"),
            ]);
    }

    private static WzValue.Canvas CreateMushroomCanvas(int frame, int w, int h, int delay, int bounceY) {
        byte[] rgba = new byte[w * h * 4];
        double cx = w / 2.0;
        double capCy = 20.0 + bounceY;
        double stemCy = 32.0 + bounceY;

        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int idx = (y * w + x) * 4;

                // Stem (cream rounded base)
                double sdx = (x - cx) / 11.0;
                double sdy = (y - stemCy) / 10.0;
                double sDist = sdx * sdx + sdy * sdy;

                // Cap (orange dome)
                double cdx = (x - cx) / 18.0;
                double cdy = (y - capCy) / 13.0;
                double cDist = cdx * cdx + cdy * cdy;

                if (y <= capCy + 4 && cDist <= 1.0) {
                    if (cDist >= 0.84) {
                        SetPixel(rgba, idx, 95, 42, 14, 255);
                    } else {
                        // Spots on mushroom cap
                        double spot1 = Math.Sqrt(Math.Pow(x - (cx - 8), 2) + Math.Pow(y - (capCy - 3), 2));
                        double spot2 = Math.Sqrt(Math.Pow(x - (cx + 7), 2) + Math.Pow(y - (capCy - 4), 2));
                        if (spot1 < 3.4 || spot2 < 3.0) {
                            SetPixel(rgba, idx, 255, 225, 165, 255);
                        } else {
                            SetPixel(rgba, idx, 245, 130, 35, 255);
                        }
                    }
                } else if (y > capCy && sDist <= 1.0) {
                    if (sDist >= 0.82) {
                        SetPixel(rgba, idx, 105, 75, 50, 255);
                    } else {
                        // Cute eyes
                        bool leftEye = Math.Abs(x - (cx - 4)) <= 1 && Math.Abs(y - (stemCy - 1)) <= 1.5;
                        bool rightEye = Math.Abs(x - (cx + 4)) <= 1 && Math.Abs(y - (stemCy - 1)) <= 1.5;
                        if (leftEye || rightEye) {
                            SetPixel(rgba, idx, 35, 25, 20, 255);
                        } else {
                            SetPixel(rgba, idx, 250, 236, 210, 255);
                        }
                    }
                }
            }
        }

        byte[] payload = WzCanvasCodec.EncodeFromRgba(w, h, 2, rgba);
        return new WzValue.Canvas(
            w,
            h,
            2,
            payload,
            [
                new WzProperty("origin", (24, 42)),
                new WzProperty("lt", (-20, -38)),
                new WzProperty("rb", (20, 0)),
                new WzProperty("head", (0, -34)),
                new WzProperty("delay", delay),
            ]);
    }

    private static void SetPixel(byte[] rgba, int idx, byte r, byte g, byte b, byte a) {
        rgba[idx] = r;
        rgba[idx + 1] = g;
        rgba[idx + 2] = b;
        rgba[idx + 3] = a;
    }
}
