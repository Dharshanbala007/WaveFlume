# Items to verify before release

Each item lives in its own method and is covered by a test. Do not ship a path marked here until an engineer has checked it against the reference.

| # | Item | Where | Reference to check | Status |
|---|---|---|---|---|
| 1 | Peak hydrodynamic force estimate `F = ρ g A_w tanh(kh)/k · W` | `WaveMathEngine.Force` | Hughes (1993) ch. 6; IITM theory manual | Implemented with full flume width **W = 3 m**: the golden case (177 N) only matches W, not b = 0.5 m as §7.10 says. For random/focusing it uses the design wave (Hs or H at Tp). Confirm. |
| 2 | Madsen (1971) 2nd-order monochromatic piston term `X0⁽²⁾ = (H²/(32h))(3cosh kh/sinh³ kh − 2/c0) sin 2ωt` | `SecondOrder.MadsenPistonAmplitude`, `TrajectoryGenerator` (ramped with E²) | Madsen (1971); manual eq. 3 with n = m | **Implemented exactly as §7.4**; golden case X2 = −0.130 mm (hand-checked). Sign/phase against eq. 3 **not yet verified**. §7.4 asks for a simulator check (Stokes bound term present, no free 2nd harmonic), but that needs a second-order wave model in the simulator, which needs the same theory. Not done; validate in the real flume. |
| 3 | General 2nd-order transfer functions `F±_nm`, `G±_nm` | `SecondOrder.SchaefferUnavailable` | Schäffer (1996), Ocean Eng. 23(1) | **Not implemented.** 2nd order for bichromatic/random/focusing throws with a clear message; 1st order works. Needs the closed forms. |
| 4 | Darbyshire spectrum (dimensionally suspect as drafted) | `Spectra.DarbyshireShape` | IITM manual spectrum page | Implemented as drafted, renormalised to Hs, **disabled** unless `Irregular.EnableDarbyshire = true` in `flume_config.json`. |
| 5 | Dalrymple (1989) oblique transfer-function correction | — | Dalrymple (1989) | Not implemented (optional per §7.8). |
| 6 | Elevated-hinge `c0` (hinge height measured from the bed) | `TransferFunctions.ElevatedHinge` | Hughes (1993); Dean & Dalrymple (1984) | Implemented as §7.2 (overflow-safe form, tested equal to the spec formula); reduces to the bottom-hinged flap. Confirm the h0 datum. |
| 7 | Cnoidal waves | `WaveMathEngine` (throws) | Dean & Dalrymple (1984) / Hughes (1993) / IITM manual | **Not implemented.** §7.5 names cnoidal theory but gives no formulas, and rule 2 forbids inventing them. Provide the reference (η(x,t), L and m from H, T, h; paddle method). |
| 8 | Stokes 2nd-order bound harmonic in the predicted elevation | `SecondOrder.StokesBoundAmplitude` | §7.4 | Formula from §7.4; tested against the deep-water limit k a²/2. |
