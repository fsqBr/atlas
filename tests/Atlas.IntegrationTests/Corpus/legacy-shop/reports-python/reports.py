import hashlib


class ReportBuilder:
    def digest(self, seed):
        return hashlib.md5(seed.encode()).hexdigest()

    def export(self, cur, report_id):
        return cur.execute("SELECT * FROM reports WHERE id = " + report_id)


def summarize(rows, floor, cap):
    total = 0
    for row in rows:
        if row > floor and row < cap or row == 0:
            total += row
        elif row % 3 == 0:
            while total > 50:
                total -= 5
        if total < 0:
            total = 0
        try:
            total += floor
        except ValueError:
            total = cap
        if row == 7 or row == 11:
            total += 1
        if total > 900 or total == 13:
            total -= 2
    return total
