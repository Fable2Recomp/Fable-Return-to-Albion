#!/usr/bin/env python3
"""Models the guest-clock estimator against a measured frame pattern.

    python3 tools/tick_clock_fit_sim.py

THE PROBLEM THIS EXISTS TO SETTLE. Everything the guest states on its tick — camera, bodies,
bone poses — is drawn at a playhead derived from a fitted clock. The guest does NOT tick at the
nominal 15 Hz; measured live in town it runs 15.0510. An origin-only estimator has to chase that
0.34%/s drift, and chasing it at 5% per sample (about 3,000 samples a second) converges inside a
single frame, which PINS the playhead to the integer tick counter. The playhead then barely moves
within a tick and jumps a whole tick at the boundary: the world freezes and lurches at 15 Hz, and
rigs stop interpolating.

Expected output. The last row is the shipped design — fit the PERIOD by least squares over the
lower envelope, slew the frequency, and bound the phase correction, which is an ordinary clock
discipline loop. The bound on the phase term IS the playback-rate guarantee:

    old: min + 5%/sample creep      median 0.011, p95 2.918   <- the stepping
    least squares on lower envelope median 1.000, p95 1.016
    LS + bounded phase correction   median 1.000, p95 1.005, never leaves 0.9947..1.0054

Keep in step with Godot/src/Engine/core/Engine_TickClock.cs.
"""

import random
TICK = 1e6/15.0
P_TRUE = 1e6/15.051
random.seed(11)

def frames(seconds=60.0, fps=44.0, spike_every=4.0, spike_ms=60.0):
    t,out,nxt=0.0,[],spike_every
    while t<seconds*1e6:
        dt=spike_ms*1000.0 if t>=nxt*1e6 else 1e6/fps
        if t>=nxt*1e6: nxt+=spike_every
        t+=dt; out.append(t)
    return out

class Old:
    name="old: min + 5%/sample creep"
    def __init__(self): self.o=None; self.p=TICK
    def obs(self,T,now):
        c=now-T*TICK
        self.o=c if self.o is None or c<self.o else self.o+(c-self.o)*0.05
    def frame(self,dt): pass
    def draw(self,now): return (now-self.o)/TICK

class LS:
    """Least squares over the lower envelope: one earliest-arrival point per tick."""
    name="least squares on lower envelope"
    N=90                      # ticks kept ~ 6 s
    def __init__(self):
        self.T={}             # tick -> earliest arrival seen
        self.p=TICK; self.o=None; self.newest=None
    def obs(self,T,now):
        cur=self.T.get(T)
        if cur is None or now<cur: self.T[T]=now
        self.newest=T if self.newest is None else max(self.newest,T)
    def frame(self,dt):
        if self.newest is None: return
        lo=self.newest-self.N
        for k in [k for k in self.T if k<lo]: del self.T[k]
        if len(self.T)<8:
            if self.o is None and self.T:
                k=min(self.T); self.o=self.T[k]-k*self.p
            return
        n=len(self.T); sx=sy=sxx=sxy=0.0
        base=self.newest
        for k,v in self.T.items():
            x=float(k-base); y=v
            sx+=x; sy+=y; sxx+=x*x; sxy+=x*y
        den=n*sxx-sx*sx
        if den<=0: return
        slope=(n*sxy-sx*sy)/den
        inter=(sy-slope*sx)/n          # arrival at tick == base
        if 0.9*TICK<slope<1.1*TICK:
            now_p, now_o = self.p, self.o
            self.p=slope
            self.o=inter-base*slope
            # keep the playhead continuous across a refit
            if now_o is not None:
                pass
    def draw(self,now): return (now-self.o)/self.p

def run(cls, lat=1.3, seconds=60.0, change_at=None, seed=11):
    random.seed(seed)
    est=cls(); fs=frames(seconds=seconds); prev=fs[0]; pd=None
    st=0; tot=0; rates=[]
    for t in fs:
        dt=t-prev; prev=t
        est.frame(dt)
        p=P_TRUE if (change_at is None or t<change_at*1e6) else P_TRUE*1.01
        newest=int(t/p)
        for _ in range(70): est.obs(newest, t+random.uniform(0,1200))
        if est.o is None: pd=None; continue
        d=est.draw(t)-lat; tot+=1
        if d>newest: st+=1
        if pd is not None and dt>0: rates.append((t/1e6,(d-pd)/(dt/p)))
        pd=d
    return est, st/max(tot,1), rates



class PLL(LS):
    """LS for frequency, bounded phase correction for offset — a clock discipline loop."""
    name="LS + bounded phase correction"
    PHASE_TOL = 0.005      # playhead may be nudged by 0.5% of elapsed time
    PERIOD_A  = 0.02       # how fast the period follows the fit
    def __init__(self):
        LS.__init__(self); self.pl=None; self.lastnow=None
    def frame(self,dt):
        if self.newest is None: return
        lo=self.newest-self.N
        for k in [k for k in self.T if k<lo]: del self.T[k]
        if len(self.T)<8:
            if self.o is None and self.T:
                k=min(self.T); self.o=self.T[k]-k*self.p
            return
        n=len(self.T); sx=sy=sxx=sxy=0.0; base=self.newest
        for k,v in self.T.items():
            x=float(k-base); sx+=x; sy+=v; sxx+=x*x; sxy+=x*v
        den=n*sxx-sx*sx
        if den<=0: return
        slope=(n*sxy-sx*sy)/den
        inter=(sy-slope*sx)/n
        if not (0.9*TICK<slope<1.1*TICK): return
        o_ls = inter-base*slope
        if self.o is None:
            self.p, self.o = slope, o_ls; return
        # frequency: slew toward the fit, holding the playhead still across the change
        now = self.lastnow if self.lastnow is not None else o_ls
        pos = (now-self.o)/self.p
        self.p += (slope-self.p)*self.PERIOD_A
        self.o  = now - pos*self.p
        # phase: bounded nudge toward where the fit says we should be
        want = (now-o_ls)/slope
        err  = want - pos
        budget = (dt/self.p)*self.PHASE_TOL
        corr = max(-budget, min(budget, err))
        self.o -= corr*self.p
    def draw(self,now):
        self.lastnow = now
        return (now-self.o)/self.p

def run2(cls, lat=1.3, seconds=60.0, change_at=None, seed=11):
    random.seed(seed)
    est=cls(); fs=frames(seconds=seconds); prev=fs[0]; pd=None
    st=0; tot=0; rates=[]
    for t in fs:
        dt=t-prev; prev=t
        est.lastnow = t if hasattr(est,'lastnow') else None
        est.frame(dt)
        p=P_TRUE if (change_at is None or t<change_at*1e6) else P_TRUE*1.01
        newest=int(t/p)
        for _ in range(70): est.obs(newest, t+random.uniform(0,1200))
        if est.o is None: pd=None; continue
        d=est.draw(t)-lat; tot+=1
        if d>newest: st+=1
        if pd is not None and dt>0: rates.append((t/1e6,(d-pd)/(dt/p)))
        pd=d
    return est, st/max(tot,1), rates

print(f"{'estimator':34} {'starve':>7} {'med':>7} {'p5':>7} {'p95':>7} {'min>8s':>8} {'max>8s':>8}")
for cls in (Old, LS, PLL):
    e,s,r=run2(cls)
    v=sorted(x[1] for x in r); n=len(v)
    late=[x[1] for x in r if x[0]>8]
    print(f"{cls.name:34} {s:7.3f} {v[n//2]:7.3f} {v[int(.05*n)]:7.3f} {v[int(.95*n)]:7.3f} "
          f"{min(late):8.4f} {max(late):8.4f}")

e,s,r=run2(PLL, change_at=30.0)
late=[x[1] for x in r if x[0]>40]
print(f"\nPLL, guest rate +1% at 30s: starvation {s:.4f}")
print(f"   after 40s: rate min {min(late):.4f} max {max(late):.4f}")
print(f"   period {e.p:.1f} vs new true {P_TRUE*1.01:.1f} ({(e.p/(P_TRUE*1.01)-1)*100:+.3f}%)")
