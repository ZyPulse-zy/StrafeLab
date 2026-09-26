"""Run with the bundled demo-python interpreter; no network or game access."""
import importlib.util
from pathlib import Path
import unittest
import pandas as pd

spec = importlib.util.spec_from_file_location("extractor", Path(__file__).resolve().parents[1]/"tools/demo-extractor.py")
extractor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(extractor)

class VelocityTests(unittest.TestCase):
    def frame(self):
        # Accelerating position: current interval differs from previous interval.
        rows=[]
        for player,base in [(1,0),(2,1000)]:
            for tick in range(70):
                rows.append(dict(steamid=player,tick=tick,game_time=20+tick/64,X=base+tick*tick/128,Y=0,Z=0))
        return pd.DataFrame(rows)
    def test_velocity_is_ending_interval_not_previous_tick_or_other_player(self):
        frame,rate=extractor.prepare_velocities(self.frame())
        self.assertEqual(rate,64)
        for player in (1,2):
            p=frame[frame.steamid==player].set_index('tick')
            self.assertTrue(pd.isna(p.loc[0,'velocity_X']))
            self.assertAlmostEqual(p.loc[20,'velocity_X'],19.5)
            self.assertAlmostEqual(p.loc[21,'velocity_X'],20.5)
    def test_missing_tick_leaves_speed_unknown(self):
        frame=self.frame();frame=frame[~((frame.steamid==1)&(frame.tick==20))]
        result,_=extractor.prepare_velocities(frame)
        self.assertTrue(pd.isna(result[(result.steamid==1)&(result.tick==21)].velocity_X.iloc[0]))
    def test_missing_clock_fails_closed(self):
        with self.assertRaises(RuntimeError):extractor.prepare_velocities(self.frame().drop(columns=['game_time']))

if __name__=='__main__':unittest.main()
