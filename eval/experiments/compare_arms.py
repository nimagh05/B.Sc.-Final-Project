import csv, random, statistics as st, sys

def load(tag):
    with open(f"eval/experiments/out/embed_results_{tag}.csv", encoding="utf-8") as f:
        return {r["id"]: r for r in csv.DictReader(f) if r["is_answerable"] == "True"}

a_tag, b_tag = sys.argv[1], sys.argv[2]
strict = len(sys.argv) > 3 and sys.argv[3] == "strict"
A, B = load(a_tag), load(b_tag)
ids = sorted(set(A) & set(B))
col = "rank_first_hit_strict" if strict else "rank_first_hit"
INF = 10**9

def rank(r):
    v = int(r[col])
    return v if v > 0 else INF

def rr(r):
    v = rank(r)
    return 0.0 if v == INF else 1.0 / v

rule = "strict span<=3" if strict else "loose"
print(f"=== {len(ids)} answerable questions: {a_tag} (A) vs {b_tag} (B) | rule: {rule} ===")
for k in (1, 3, 5, 10):
    a_only = sum(1 for i in ids if rank(A[i]) <= k < rank(B[i]))
    b_only = sum(1 for i in ids if rank(B[i]) <= k < rank(A[i]))
    print(f"  hit@{k:<2d}: only A = {a_only}, only B = {b_only}")
diffs = [rr(B[i]) - rr(A[i]) for i in ids]
random.seed(1)
boots = sorted(st.mean(random.choices(diffs, k=len(diffs))) for _ in range(5000))
print(f"  mean dRR (B - A) = {st.mean(diffs):+.4f}, 95% bootstrap CI = [{boots[125]:+.4f}, {boots[4875]:+.4f}]")
for q in ("q016", "q034", "q050", "q055", "q056"):
    if q in A and q in B:
        ra, rb = rank(A[q]), rank(B[q])
        print(f"  {q}: rank A = {'-' if ra == INF else ra} | rank B = {'-' if rb == INF else rb}")