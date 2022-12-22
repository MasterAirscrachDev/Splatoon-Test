using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ProjectileSystem : MonoBehaviour
{
    int splashSize = 10, team = 1;
    bool canRespawn = true;
    public void Setup(Vector3 velocity, float force, int splashSize, int team, bool canRespawn = true){
        this.splashSize = splashSize; this.team = team; this.canRespawn = canRespawn;
        GetComponent<Rigidbody>().velocity = velocity;
        GetComponent<Rigidbody>().AddForce(transform.forward * force);
        transform.GetChild(0).GetComponent<InkEmitter>().Init(splashSize, team);
    }
    void Update(){
        if(canRespawn && Random.Range(0, 100) < 1){
            GameObject newParticle = Instantiate(gameObject, transform.position, Quaternion.identity);
            newParticle.GetComponent<ProjectileSystem>().Setup(Vector3.zero, 0, splashSize, team, false);
        }
        if(transform.position.y < -10){
            Destroy(gameObject);
        }
    }
    public void DeleteProjectile(){
        //Destroy(transform.GetChild(0).gameObject);
        Destroy(gameObject);
    }
}
