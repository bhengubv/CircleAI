/*
 * test_integration.c — CircleAI.Integration (C11 port) verification of the six
 * contract records' deep-copy / free primitives + HaEntity attribute lookup.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_calendar_event_copy(void) {
    char *att[2] = {(char *)"a@x.io", (char *)"b@x.io"};
    ca_int_calendar_event_t e;
    memset(&e, 0, sizeof(e));
    e.event_id = (char *)"ev1";
    e.calendar_id = (char *)"cal";
    e.title = (char *)"Sync";
    e.has_description = true;
    e.description = (char *)"desc";
    e.has_location = false;
    e.start_utc_ms = 1000;
    e.end_utc_ms = 2000;
    e.is_all_day = false;
    e.attendees = att;
    e.attendees_count = 2;

    ca_int_calendar_event_t c;
    assert(ca_int_calendar_event_copy(&c, &e));
    assert(strcmp(c.event_id, "ev1") == 0);
    assert(strcmp(c.title, "Sync") == 0);
    assert(c.has_description && strcmp(c.description, "desc") == 0);
    assert(!c.has_location && c.location == NULL);
    assert(c.attendees_count == 2 && strcmp(c.attendees[1], "b@x.io") == 0);
    assert(c.start_utc_ms == 1000 && c.end_utc_ms == 2000);
    /* deep copy: independent buffers */
    assert(c.event_id != e.event_id);
    ca_int_calendar_event_free(&c);
    printf("  calendar_event_copy: ok\n");
}

static void test_email_message_copy(void) {
    char *to[1] = {(char *)"you@x.io"};
    char *labels[1] = {(char *)"UNREAD"};
    ca_int_email_message_t m;
    memset(&m, 0, sizeof(m));
    m.message_id = (char *)"m1";
    m.from = (char *)"me@x.io";
    m.to = to; m.to_count = 1;
    m.subject = (char *)"Hi";
    m.body_text = (char *)"body";
    m.received_utc_ms = 500;
    m.unread = true;
    m.labels = labels; m.labels_count = 1;

    ca_int_email_message_t c;
    assert(ca_int_email_message_copy(&c, &m));
    assert(strcmp(c.from, "me@x.io") == 0 && c.unread);
    assert(c.to_count == 1 && strcmp(c.to[0], "you@x.io") == 0);
    assert(c.labels_count == 1 && strcmp(c.labels[0], "UNREAD") == 0);
    ca_int_email_message_free(&c);
    printf("  email_message_copy: ok\n");
}

static void test_news_item_copy(void) {
    char *tags[2] = {(char *)"tech", (char *)"ai"};
    ca_int_news_item_t n;
    memset(&n, 0, sizeof(n));
    n.item_id = (char *)"i1"; n.source_id = (char *)"src";
    n.title = (char *)"T"; n.summary = (char *)"S";
    n.url = (char *)"https://x.io/a"; n.published_utc_ms = 42;
    n.tags = tags; n.tags_count = 2;

    ca_int_news_item_t c;
    assert(ca_int_news_item_copy(&c, &n));
    assert(strcmp(c.url, "https://x.io/a") == 0 && c.published_utc_ms == 42);
    assert(c.tags_count == 2 && strcmp(c.tags[0], "tech") == 0);
    ca_int_news_item_free(&c);
    printf("  news_item_copy: ok\n");
}

static void test_weather_route_copy(void) {
    ca_int_weather_sample_t w;
    memset(&w, 0, sizeof(w));
    w.at_utc_ms = 7; w.temp_c = 21.5; w.feels_like_c = 20.0;
    w.precip_mm = 0.0; w.wind_kph = 10.8; w.cloud_pct = 40;
    w.condition = (char *)"partly cloudy";
    ca_int_weather_sample_t wc;
    assert(ca_int_weather_sample_copy(&wc, &w));
    assert(wc.temp_c == 21.5 && wc.cloud_pct == 40 &&
           strcmp(wc.condition, "partly cloudy") == 0);
    ca_int_weather_sample_free(&wc);

    ca_int_route_point_t pts[2] = {{1.0, 2.0}, {3.0, 4.0}};
    ca_int_route_estimate_t r;
    memset(&r, 0, sizeof(r));
    r.distance_km = 12.3; r.duration_ms = 60000; r.polyline = pts; r.polyline_count = 2;
    ca_int_route_estimate_t rc;
    assert(ca_int_route_estimate_copy(&rc, &r));
    assert(rc.distance_km == 12.3 && rc.duration_ms == 60000);
    assert(rc.polyline_count == 2 && rc.polyline[1].lat == 3.0 &&
           rc.polyline[1].lon == 4.0);
    assert(rc.polyline != pts); /* deep copy */
    ca_int_route_estimate_free(&rc);
    printf("  weather_route_copy: ok\n");
}

static void test_ha_entity_copy_and_attr(void) {
    ca_int_attr_pair_t attrs[2] = {
        {(char *)"friendly_name", (char *)"Kitchen Light"},
        {(char *)"brightness", (char *)"255"},
    };
    ca_int_ha_entity_t e;
    memset(&e, 0, sizeof(e));
    e.entity_id = (char *)"light.kitchen";
    e.friendly_name = (char *)"Kitchen Light";
    e.domain = (char *)"light";
    e.state = (char *)"on";
    e.attributes = attrs; e.attributes_count = 2;

    ca_int_ha_entity_t c;
    assert(ca_int_ha_entity_copy(&c, &e));
    assert(strcmp(c.entity_id, "light.kitchen") == 0);
    assert(c.attributes_count == 2);
    /* attr lookup (Ordinal). */
    const char *b = ca_int_ha_entity_attr(&c, "brightness");
    assert(b && strcmp(b, "255") == 0);
    assert(ca_int_ha_entity_attr(&c, "nope") == NULL);
    /* Ordinal (case-sensitive) miss. */
    assert(ca_int_ha_entity_attr(&c, "Brightness") == NULL);
    ca_int_ha_entity_free(&c);
    printf("  ha_entity_copy_and_attr: ok\n");
}

int main(void) {
    test_calendar_event_copy();
    test_email_message_copy();
    test_news_item_copy();
    test_weather_route_copy();
    test_ha_entity_copy_and_attr();
    printf("test_integration: all assertions passed\n");
    return 0;
}
