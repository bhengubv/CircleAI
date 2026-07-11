// education_board.test.ts
// Verifies the CircleAI.Education port: courses, lessons ordered by OrderIndex,
// enrolment + progress update, and average-progress rollup (0 when empty).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryEducationBoard,
  EducationDomainContext,
  course,
  lesson,
  studentRecord,
} from "../src/education/index";

describe("InMemoryEducationBoard", () => {
  it("adds and retrieves courses", () => {
    const b = new InMemoryEducationBoard();
    b.addCourse(course("c1", "Algebra", "Maths", "Grade 8"));
    assert.equal(b.getCourse("c1")?.name, "Algebra");
    assert.equal(b.getCourse("nope"), undefined);
  });

  it("lists lessons ordered by OrderIndex ascending", () => {
    const b = new InMemoryEducationBoard();
    b.addLesson(lesson("l3", "c1", "Third", 600_000, 3));
    b.addLesson(lesson("l1", "c1", "First", 600_000, 1));
    b.addLesson(lesson("l2", "c1", "Second", 600_000, 2));
    b.addLesson(lesson("lx", "c2", "Other", 600_000, 1));
    assert.deepEqual(
      b.lessonsFor("c1").map((l) => l.lessonId),
      ["l1", "l2", "l3"],
    );
  });

  it("enrols students, updates progress, and computes the average", () => {
    const b = new InMemoryEducationBoard();
    b.enrol(studentRecord("s1", "A", "c1", 20));
    b.enrol(studentRecord("s2", "B", "c1", 80));
    b.enrol(studentRecord("s3", "C", "c2", 50));
    b.updateProgress("s1", 40);
    assert.equal(b.avgProgressFor("c1"), (40 + 80) / 2);
    assert.equal(b.studentsFor("c1").length, 2);
  });

  it("avgProgressFor an empty course is 0", () => {
    const b = new InMemoryEducationBoard();
    assert.equal(b.avgProgressFor("empty"), 0);
  });

  it("updateProgress for an unknown student throws", () => {
    const b = new InMemoryEducationBoard();
    assert.throws(() => b.updateProgress("ghost", 10), /Unknown student ghost/);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(EducationDomainContext.systemPromptSnippet.includes("[DOMAIN: Education]"));
    assert.deepEqual(EducationDomainContext.complianceFlags, ["SASA", "CAPS_NCS", "POPIA", "PAIA"]);
    assert.deepEqual(EducationDomainContext.suggestedTools, [
      "learning_management",
      "document_editor",
      "assessment_tools",
      "web_search",
    ]);
  });
});
